namespace NzbWebDAV.Hosting;

public enum NzbdavRole
{
    All,
    Control,
    Gateway,
    WorkerDownload,
    WorkerVerify,
    WorkerRepair,
    Ui,
}

public enum NzbdavCapability
{
    Database,
    AdminApi,
    SabApi,
    ArrBackground,
    Maintenance,
    WebDav,
    ProviderPool,
    SparseCache,
    DownloadWorker,
    VerifyWorker,
    RepairWorker,
    InternalRpc,
    UiFrontend,
}
