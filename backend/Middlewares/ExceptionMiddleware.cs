using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Logging;
using NzbWebDAV.Security;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Middlewares;

public class ExceptionMiddleware(RequestDelegate next, ConfigManager configManager)
{
    private static readonly ConcurrentDictionary<Guid, DateTime> RecentRepairTriggers = new();
    private static readonly TimeSpan RepairDedupeWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CleanupThreshold = TimeSpan.FromMinutes(5);
    private static int _callCount;

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception e) when (IsCausedByAbortedRequest(e, context))
        {
            if (context.Response.HasStarted) throw;
            await PublicFailureContract.WriteAsync(
                context,
                499,
                PublicFailureContract.ClientClosedRequest()).ConfigureAwait(false);
        }
        catch (UsenetArticleNotFoundException e)
        {
            if (context.Response.HasStarted) throw;
            HealthCheckService.RememberMissingSegmentId(e.SegmentId);
            var failure = PublicFailureContract.ContentUnavailable();
            await PublicFailureContract.WriteAsync(
                context,
                StatusCodes.Status404NotFound,
                failure,
                includeBody: false).ConfigureAwait(false);
            Log.ForContext(
                    V1SafeConsoleFormatter.EventIdPropertyName,
                    V1OperationalEventId.ContentArticleMissing)
                .Error(
                "content_article_missing CorrelationId={CorrelationId} DavItemId={DavItemId}",
                failure.CorrelationId,
                GetDavItemId(context));

            if (context.Items["DavItem"] is DavItem davItem)
                ScheduleRepair(davItem.Id);
        }
        catch (RetryableDownloadException) when (IsDavItemRequest(context))
        {
            if (context.Response.HasStarted) throw;
            var failure = PublicFailureContract.ContentTemporarilyUnavailable();
            await PublicFailureContract.WriteAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                failure,
                includeBody: false).ConfigureAwait(false);
            context.Response.Headers.RetryAfter = "30";
            Log.ForContext(
                    V1SafeConsoleFormatter.EventIdPropertyName,
                    V1OperationalEventId.ContentProviderTemporarilyUnavailable)
                .Warning(
                "content_provider_temporarily_unavailable CorrelationId={CorrelationId} DavItemId={DavItemId}",
                failure.CorrelationId,
                GetDavItemId(context));
        }
        catch (SeekPositionNotFoundException)
        {
            if (context.Response.HasStarted) throw;
            var failure = PublicFailureContract.ContentRangeUnavailable();
            await PublicFailureContract.WriteAsync(
                context,
                StatusCodes.Status404NotFound,
                failure,
                includeBody: false).ConfigureAwait(false);
            Log.ForContext(
                    V1SafeConsoleFormatter.EventIdPropertyName,
                    V1OperationalEventId.ContentRangeUnavailable)
                .Error(
                "content_range_unavailable CorrelationId={CorrelationId} DavItemId={DavItemId}",
                failure.CorrelationId,
                GetDavItemId(context));
        }
        catch (FileNotFoundException) when (IsDavItemRequest(context))
        {
            if (context.Response.HasStarted) throw;
            var failure = PublicFailureContract.ContentUnavailable();
            await PublicFailureContract.WriteAsync(
                context,
                StatusCodes.Status404NotFound,
                failure,
                includeBody: false).ConfigureAwait(false);
            Log.ForContext(
                    V1SafeConsoleFormatter.EventIdPropertyName,
                    V1OperationalEventId.ContentMetadataMissing)
                .Warning(
                "content_metadata_missing CorrelationId={CorrelationId} DavItemId={DavItemId}",
                failure.CorrelationId,
                GetDavItemId(context));

            if (context.Items["DavItem"] is DavItem davItem)
                ScheduleRepair(davItem.Id);
        }
        catch (BadHttpRequestException)
        {
            if (context.Response.HasStarted) throw;
            await PublicFailureContract.WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                PublicFailureContract.InvalidRequest()).ConfigureAwait(false);
        }
        catch (Exception) when (IsDavItemRequest(context))
        {
            if (context.Response.HasStarted) throw;
            var failure = PublicFailureContract.InternalError();
            await PublicFailureContract.WriteAsync(
                context,
                StatusCodes.Status500InternalServerError,
                failure,
                includeBody: false).ConfigureAwait(false);
            Log.ForContext(
                    V1SafeConsoleFormatter.EventIdPropertyName,
                    V1OperationalEventId.ContentReadFailure)
                .Error(
                "content_read_failed CorrelationId={CorrelationId} DavItemId={DavItemId}",
                failure.CorrelationId,
                GetDavItemId(context));
        }
        catch (Exception)
        {
            if (context.Response.HasStarted) throw;
            var failure = PublicFailureContract.InternalError();
            await PublicFailureContract.WriteAsync(
                context,
                StatusCodes.Status500InternalServerError,
                failure).ConfigureAwait(false);
            Log.ForContext(
                    V1SafeConsoleFormatter.EventIdPropertyName,
                    V1OperationalEventId.RequestFailure)
                .Error("request_failed CorrelationId={CorrelationId}", failure.CorrelationId);
        }

        CleanupStaleEntries();
    }

    private bool IsCausedByAbortedRequest(Exception e, HttpContext context)
    {
        var isAffectedException = e is OperationCanceledException or EndOfStreamException;
        var isRequestAborted = context.RequestAborted.IsCancellationRequested ||
                               SigtermUtil.GetCancellationToken().IsCancellationRequested;
        return isAffectedException && isRequestAborted;
    }

    private static Guid? GetDavItemId(HttpContext context) =>
        context.Items["DavItem"] is DavItem davItem ? davItem.Id : null;

    private static bool IsDavItemRequest(HttpContext context)
    {
        return context.Items["DavItem"] is DavItem;
    }

    private void ScheduleRepair(Guid davItemId)
    {
        if (!configManager.IsRepairJobEnabled())
            return;

        var now = DateTime.UtcNow;
        var isDuplicate = false;
        RecentRepairTriggers.AddOrUpdate(
            davItemId,
            _ => now,
            (_, existing) =>
            {
                if (now - existing < RepairDedupeWindow)
                {
                    isDuplicate = true;
                    return existing;
                }

                return now;
            }
        );

        if (isDuplicate)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await using var dbContext = DavDatabaseContextRuntimeFactory.Create();
                var item = await dbContext.Items.FindAsync(davItemId).ConfigureAwait(false);
                if (item == null)
                    return;

                var urgent = DateTimeOffset.UnixEpoch;
                if (item.NextHealthCheck == urgent)
                    return;

                item.NextHealthCheck = urgent;
                var dbClient = new DavDatabaseClient(dbContext);
                await dbClient.EnqueueWorkerJobAsync(
                        WorkerJob.JobKind.Repair,
                        davItemId,
                        priority: 1,
                        now: DateTimeOffset.UtcNow)
                    .ConfigureAwait(false);
                Log.ForContext(
                        V1SafeConsoleFormatter.EventIdPropertyName,
                        V1OperationalEventId.DynamicRepairScheduled)
                    .Information("dynamic_repair_scheduled DavItemId={DavItemId}", item.Id);
            }
            catch (Exception)
            {
                Log.ForContext(
                        V1SafeConsoleFormatter.EventIdPropertyName,
                        V1OperationalEventId.DynamicRepairScheduleFailure)
                    .Warning("dynamic_repair_schedule_failed DavItemId={DavItemId}", davItemId);
            }
        });
    }

    private static void CleanupStaleEntries()
    {
        if (Interlocked.Increment(ref _callCount) % 100 != 0)
            return;

        var cutoff = DateTime.UtcNow - CleanupThreshold;
        foreach (var kvp in RecentRepairTriggers)
        {
            if (kvp.Value < cutoff)
                RecentRepairTriggers.TryRemove(kvp.Key, out _);
        }
    }
}
