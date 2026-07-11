namespace NzbWebDAV.Hosting;

public static class NzbdavRoleCapabilities
{
    private static readonly IReadOnlyDictionary<NzbdavRole, IReadOnlySet<NzbdavCapability>> Map =
        new Dictionary<NzbdavRole, IReadOnlySet<NzbdavCapability>>
        {
            [NzbdavRole.Control] = Set(
                NzbdavCapability.Database, NzbdavCapability.AdminApi,
                NzbdavCapability.SabApi, NzbdavCapability.ArrBackground,
                NzbdavCapability.Maintenance, NzbdavCapability.InternalRpc),
            [NzbdavRole.Gateway] = Set(
                NzbdavCapability.WebDav, NzbdavCapability.ProviderPool,
                NzbdavCapability.SparseCache, NzbdavCapability.InternalRpc),
            [NzbdavRole.WorkerDownload] = Set(
                NzbdavCapability.DownloadWorker, NzbdavCapability.InternalRpc),
            [NzbdavRole.WorkerVerify] = Set(
                NzbdavCapability.VerifyWorker, NzbdavCapability.InternalRpc),
            [NzbdavRole.WorkerRepair] = Set(
                NzbdavCapability.RepairWorker, NzbdavCapability.InternalRpc),
            [NzbdavRole.Ui] = Set(NzbdavCapability.UiFrontend),
        };

    private static readonly IReadOnlySet<NzbdavCapability> All =
        Map.Values.SelectMany(x => x).ToHashSet();

    public static IReadOnlySet<NzbdavCapability> For(NzbdavRole role) =>
        role == NzbdavRole.All ? All : Map[role];

    private static IReadOnlySet<NzbdavCapability> Set(params NzbdavCapability[] values) =>
        values.ToHashSet();
}
