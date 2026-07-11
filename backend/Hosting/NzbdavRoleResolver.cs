namespace NzbWebDAV.Hosting;

public static class NzbdavRoleResolver
{
    public static NzbdavRole Resolve(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "all" => NzbdavRole.All,
            "control" => NzbdavRole.Control,
            "gateway" => NzbdavRole.Gateway,
            "worker-download" => NzbdavRole.WorkerDownload,
            "worker-verify" => NzbdavRole.WorkerVerify,
            "worker-repair" => NzbdavRole.WorkerRepair,
            "ui" => NzbdavRole.Ui,
            _ => throw new InvalidOperationException($"Unsupported NZBDAV_ROLE '{value}'.")
        };
    }
}
