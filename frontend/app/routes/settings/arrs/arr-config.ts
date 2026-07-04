export interface ConnectionDetails {
    Host: string;
    ApiKey: string;
}

export interface QueueRule {
    Message: string;
    Action: number;
}

export interface ArrConfig {
    RadarrInstances: ConnectionDetails[];
    SonarrInstances: ConnectionDetails[];
    LidarrInstances: ConnectionDetails[];
    QueueRules: QueueRule[];
    Prioritization: ArrPrioritizationOptions;
    SearchNudge: ArrSearchNudgeOptions;
}

export interface ArrPrioritizationOptions {
    Enabled: boolean;
    Mode: "report" | "apply";
    RecomputeIntervalSeconds: number;
    MaxAutomaticPriority: number;
}

export interface ArrSearchNudgeOptions {
    Enabled: boolean;
    Mode: "report" | "apply";
    IntervalSeconds: number;
    CooldownSeconds: number;
    MaxCommandsPerHour: number;
    SonarrBatchSize: number;
    RadarrBatchSize: number;
    ConcurrentCommandsPerInstance: number;
}

const emptyArrConfig = (): ArrConfig => ({
    RadarrInstances: [],
    SonarrInstances: [],
    LidarrInstances: [],
    QueueRules: [],
    Prioritization: defaultPrioritization(),
    SearchNudge: defaultSearchNudge(),
});

export function parseArrConfig(rawValue: string | undefined): ArrConfig {
    if (!rawValue?.trim()) return emptyArrConfig();

    try {
        const parsed = JSON.parse(rawValue);
        return {
            RadarrInstances: normalizeInstances(parsed?.RadarrInstances),
            SonarrInstances: normalizeInstances(parsed?.SonarrInstances),
            LidarrInstances: normalizeInstances(parsed?.LidarrInstances),
            QueueRules: normalizeQueueRules(parsed?.QueueRules),
            Prioritization: normalizePrioritization(parsed?.Prioritization),
            SearchNudge: normalizeSearchNudge(parsed?.SearchNudge),
        };
    } catch {
        return emptyArrConfig();
    }
}

export function serializeArrConfig(config: ArrConfig): string {
    return JSON.stringify({
        RadarrInstances: normalizeInstances(config.RadarrInstances),
        SonarrInstances: normalizeInstances(config.SonarrInstances),
        LidarrInstances: normalizeInstances(config.LidarrInstances),
        QueueRules: normalizeQueueRules(config.QueueRules),
        Prioritization: normalizePrioritization(config.Prioritization),
        SearchNudge: normalizeSearchNudge(config.SearchNudge),
    });
}

function normalizeInstances(value: unknown): ConnectionDetails[] {
    if (!Array.isArray(value)) return [];
    return value
        .filter((instance): instance is Partial<ConnectionDetails> => !!instance && typeof instance === "object")
        .map(instance => ({
            Host: typeof instance.Host === "string" ? instance.Host : "",
            ApiKey: typeof instance.ApiKey === "string" ? instance.ApiKey : "",
        }));
}

function normalizeQueueRules(value: unknown): QueueRule[] {
    if (!Array.isArray(value)) return [];
    return value
        .filter((rule): rule is Partial<QueueRule> => !!rule && typeof rule === "object")
        .map(rule => ({
            Message: typeof rule.Message === "string" ? rule.Message : "",
            Action: typeof rule.Action === "number" ? rule.Action : Number(rule.Action ?? 0),
        }))
        .filter(rule => rule.Message.trim().length > 0 && Number.isFinite(rule.Action));
}

function defaultPrioritization(): ArrPrioritizationOptions {
    return {
        Enabled: false,
        Mode: "report",
        RecomputeIntervalSeconds: 300,
        MaxAutomaticPriority: 1,
    };
}

function defaultSearchNudge(): ArrSearchNudgeOptions {
    return {
        Enabled: false,
        Mode: "report",
        IntervalSeconds: 1800,
        CooldownSeconds: 21600,
        MaxCommandsPerHour: 20,
        SonarrBatchSize: 10,
        RadarrBatchSize: 5,
        ConcurrentCommandsPerInstance: 1,
    };
}

function normalizePrioritization(value: unknown): ArrPrioritizationOptions {
    const defaults = defaultPrioritization();
    if (!value || typeof value !== "object") return defaults;
    const options = value as Partial<ArrPrioritizationOptions>;
    return {
        Enabled: Boolean(options.Enabled),
        Mode: options.Mode === "apply" ? "apply" : "report",
        RecomputeIntervalSeconds: clampNumber(options.RecomputeIntervalSeconds, 30, 3600, defaults.RecomputeIntervalSeconds),
        MaxAutomaticPriority: clampNumber(options.MaxAutomaticPriority, -1, 1, defaults.MaxAutomaticPriority),
    };
}

function normalizeSearchNudge(value: unknown): ArrSearchNudgeOptions {
    const defaults = defaultSearchNudge();
    if (!value || typeof value !== "object") return defaults;
    const options = value as Partial<ArrSearchNudgeOptions>;
    return {
        Enabled: Boolean(options.Enabled),
        Mode: options.Mode === "apply" ? "apply" : "report",
        IntervalSeconds: clampNumber(options.IntervalSeconds, 300, 86400, defaults.IntervalSeconds),
        CooldownSeconds: clampNumber(options.CooldownSeconds, 300, 604800, defaults.CooldownSeconds),
        MaxCommandsPerHour: clampNumber(options.MaxCommandsPerHour, 1, 200, defaults.MaxCommandsPerHour),
        SonarrBatchSize: clampNumber(options.SonarrBatchSize, 1, 100, defaults.SonarrBatchSize),
        RadarrBatchSize: clampNumber(options.RadarrBatchSize, 1, 50, defaults.RadarrBatchSize),
        ConcurrentCommandsPerInstance: clampNumber(
            options.ConcurrentCommandsPerInstance,
            1,
            4,
            defaults.ConcurrentCommandsPerInstance),
    };
}

function clampNumber(value: unknown, min: number, max: number, fallback: number): number {
    const parsed = typeof value === "number" ? value : Number(value);
    if (!Number.isFinite(parsed)) return fallback;
    return Math.min(max, Math.max(min, Math.round(parsed)));
}
