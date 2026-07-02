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
}

const emptyArrConfig = (): ArrConfig => ({
    RadarrInstances: [],
    SonarrInstances: [],
    LidarrInstances: [],
    QueueRules: [],
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
