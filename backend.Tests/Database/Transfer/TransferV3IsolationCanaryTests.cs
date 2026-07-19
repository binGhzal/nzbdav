using System.Reflection;
using System.Text.RegularExpressions;
using backend.Tests.Database;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Transfer;
using NzbWebDAV.Hosting;

namespace backend.Tests.Database.Transfer;

public sealed class TransferV3IsolationCanaryTests
{
    private static readonly string[] PhaseThreeRuntimeTypeNames =
    [
        nameof(TransferV3SnapshotExporter),
        nameof(TransferV3SnapshotVerifier),
        nameof(TransferV3VerifiedSnapshot),
        nameof(TransferV3SealedSnapshotStage),
    ];

    [Fact]
    public void ProgramRefusesBothV3KindsBeforeProviderPathOrRuntimeIo()
    {
        var source = File.ReadAllText(SqliteContractTestSupport.AbsolutePath("backend/Program.cs"));
        var runStart = source.IndexOf("private static async Task RunAsync", StringComparison.Ordinal);
        var runEnd = source.IndexOf("private static void", runStart, StringComparison.Ordinal);
        Assert.True(runStart >= 0 && runEnd > runStart);
        var main = source[runStart..runEnd];

        var parse = main.IndexOf("MaintenanceCommandLine.Parse(args)", StringComparison.Ordinal);
        var exportRefusal = main.IndexOf(
            "MaintenanceCommandKind.ExportV3Unavailable",
            StringComparison.Ordinal);
        var importRefusal = main.IndexOf(
            "MaintenanceCommandKind.ImportV3Unavailable",
            StringComparison.Ordinal);
        var unavailableThrow = main.IndexOf(
            "MaintenanceCommandLine.TransferV3UnavailableMessage",
            StringComparison.Ordinal);
        var providerGate = main.IndexOf("ThrowIfPostgreSqlUnavailable()", StringComparison.Ordinal);
        var dotenv = main.IndexOf("EnvironmentUtil.LoadDotEnvFile()", StringComparison.Ordinal);
        var sqliteRuntime = main.IndexOf("ReadLoadedRuntimeAsync", StringComparison.Ordinal);
        var runtimeContext = main.IndexOf(
            "DavDatabaseContextRuntimeFactory.Create()",
            StringComparison.Ordinal);

        Assert.True(parse >= 0 && parse < exportRefusal);
        Assert.True(exportRefusal < importRefusal);
        Assert.True(importRefusal < unavailableThrow);
        Assert.True(unavailableThrow < providerGate);
        Assert.True(providerGate < dotenv);
        Assert.True(dotenv < sqliteRuntime);
        Assert.True(sqliteRuntime < runtimeContext);
    }

    [Fact]
    public void ProgramMainAwaitsRunAsyncAndUsesOnlyFixedStartupFailureBoundary()
    {
        var source = File.ReadAllText(SqliteContractTestSupport.AbsolutePath("backend/Program.cs"));
        var mainStart = source.IndexOf("static async Task<int> Main", StringComparison.Ordinal);
        var runStart = source.IndexOf("private static async Task RunAsync", StringComparison.Ordinal);
        Assert.True(mainStart >= 0 && runStart > mainStart);
        var main = source[mainStart..runStart];

        var awaitRun = main.IndexOf("await RunAsync(args).ConfigureAwait(false)", StringComparison.Ordinal);
        var failureCatch = main.IndexOf("catch (Exception exception)", StringComparison.Ordinal);
        var fixedOutput = main.IndexOf(
            "Console.Error.WriteLine(StartupFailureContract.Format(exception))",
            StringComparison.Ordinal);
        Assert.True(awaitRun >= 0 && awaitRun < failureCatch);
        Assert.True(failureCatch < fixedOutput);
        Assert.DoesNotContain("exception.Message", main, StringComparison.Ordinal);
        Assert.DoesNotContain("exception.ToString", main, StringComparison.Ordinal);
    }

    [Fact]
    public void EntrypointHasNoV3AllowlistOrUsageSurface()
    {
        var source = File.ReadAllText(SqliteContractTestSupport.AbsolutePath("entrypoint.sh"));

        Assert.DoesNotContain("--db-export-v3", source, StringComparison.Ordinal);
        Assert.DoesNotContain("--db-import-v3", source, StringComparison.Ordinal);
        Assert.Contains("*)\n            return 64", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PostgreSqlAndV3HaveNoSuccessfulMaintenanceKind()
    {
        var v3Kinds = Enum.GetNames<MaintenanceCommandKind>()
            .Where(name => name.Contains("V3", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["ExportV3Unavailable", "ImportV3Unavailable"], v3Kinds);
        Assert.DoesNotContain(
            Enum.GetNames<TransferV3ImportStateKind>(),
            name => name.Contains("Complete", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Success", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("PostgreSQL runtime is disabled", MaintenanceCommandLine.PostgreSqlUnavailableMessage);
        Assert.Contains("Use NZBDAV_DATABASE_PROVIDER=sqlite", MaintenanceCommandLine.PostgreSqlUnavailableMessage);
        Assert.DoesNotContain("not installed", DatabaseMigrator.PostgreSqlRefusalMessage);
        Assert.Contains("public migrator", DatabaseMigrator.PostgreSqlRefusalMessage);
        foreach (var type in new[]
                 {
                     typeof(TransferV3SnapshotExporter),
                     typeof(TransferV3SnapshotVerifier),
                     typeof(TransferV3VerifiedSnapshot),
                     typeof(TransferV3SealedSnapshotStage),
                 })
        {
            Assert.False(type.IsPublic || type.IsNestedPublic, type.FullName);
        }
    }

    [Fact]
    public void LegacyDatabaseTransferServiceCannotReachPhaseThree()
    {
        var servicePath = SqliteContractTestSupport.AbsolutePath(
            "backend/Database/DatabaseTransferService.cs");
        var source = File.ReadAllText(servicePath);
        var publicMethods = typeof(DatabaseTransferService)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["ExportJsonAsync", "ImportJsonAsync"], publicMethods);
        Assert.Equal(2, DatabaseTransferSnapshot.CurrentVersion);
        var transferIdentifiers = Regex.Matches(
                source,
                @"\bTransferV3[A-Za-z0-9_]+\b",
                RegexOptions.CultureInvariant)
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal([nameof(TransferV3ReservedConfigPolicy)], transferIdentifiers);
    }

    [Fact]
    public void SourceSideTransferComponentsAcceptNoTargetContextOrRuntimeBlobStore()
    {
        var transferDirectory = SqliteContractTestSupport.AbsolutePath(
            "backend/Database/Transfer");
        var phaseTwoImportStateStore = Path.Combine(
            transferDirectory,
            "TransferV3ImportStateStore.cs");
        var forbiddenTargetTypes = new[]
        {
            "DavDatabaseContext",
            "DbContext",
            "DbConnection",
            "NpgsqlConnection",
            "NpgsqlCommand",
            "NpgsqlDataSource",
            "BlobStore",
            "DatabaseTransferService",
        };

        foreach (var path in Directory.EnumerateFiles(
                     transferDirectory,
                     "*.cs",
                     SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(path, phaseTwoImportStateStore, StringComparison.Ordinal))
                continue;
            var source = File.ReadAllText(path).Replace(
                "\"NzbWebDAV.Database.DavDatabaseContext\"",
                "\"reviewed-source-context\"",
                StringComparison.Ordinal);
            foreach (var forbidden in forbiddenTargetTypes)
            {
                Assert.DoesNotContain(
                    forbidden,
                    source,
                    StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void PhaseThreeRuntimeTypesAreUnreachableOutsidePrivateTransferNamespace()
    {
        var backendDirectory = SqliteContractTestSupport.AbsolutePath("backend");
        var transferDirectory = SqliteContractTestSupport.AbsolutePath(
            "backend/Database/Transfer");
        var outsideSources = Directory.EnumerateFiles(
                backendDirectory,
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(
                               transferDirectory + Path.DirectorySeparatorChar,
                               StringComparison.Ordinal)
                           && !path.Contains(
                               $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                               StringComparison.Ordinal)
                           && !path.Contains(
                               $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                               StringComparison.Ordinal));

        foreach (var path in outsideSources)
        {
            var source = File.ReadAllText(path);
            foreach (var typeName in PhaseThreeRuntimeTypeNames)
                Assert.DoesNotContain(typeName, source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ContractReadmeStatesPhaseThreeBoundaryWithoutCompletionClaim()
    {
        var source = Regex.Replace(
            File.ReadAllText(SqliteContractTestSupport.AbsolutePath(
                "backend/Database/Transfer/Contracts/README.md")),
            @"\s+",
            " ");

        Assert.Contains("## Phase 3 private source snapshot and sealed stage", source);
        Assert.Contains("No Phase 3 type accepts a target database connection or context", source);
        Assert.Contains("no successful public command or transfer-completion claim", source);
        Assert.Contains("not a backup feature", source);
        Assert.Contains("SIGKILL, daemon loss, or host power loss", source);
    }

    [Fact]
    public void ProviderPlanDistinguishesExporterAndSealedStageResiduePolicies()
    {
        var source = Regex.Replace(
            File.ReadAllText(SqliteContractTestSupport.AbsolutePath(
                "docs/superpowers/plans/2026-07-11-nzbdav-provider-specific-migrations.md")),
            @"\s+",
            " ");

        Assert.Contains("snapshot-exporter residue policy", source);
        Assert.Contains("sealed-stage residue policy", source);
        Assert.Contains("quiescent same-UID threat model", source);
        Assert.Contains("identity check immediately followed by `unlinkat(AT_REMOVEDIR)`", source);
        Assert.Contains("does not claim an atomic conditional unlink", source);
        Assert.Contains("no-follow opens candidate roots only for classification", source);
        Assert.Contains("never enumerates or opens their contents", source);
        Assert.Contains("never deletes anything", source);
        Assert.Contains("Linux x64/arm64 and macOS arm64", source);
        Assert.Contains("macOS x64 fails closed", source);
    }
}
