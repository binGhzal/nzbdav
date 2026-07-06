import type { Route } from "./+types/route";
import { backendClient, type ConfigItem } from "~/clients/backend-client.server";

export async function action({ request }: Route.ActionArgs) {
    // get the ConfigItems to update
    const formData = await request.formData();
    const configJson = formData.get("config");
    if (typeof configJson !== "string" || configJson.trim() === "") {
        return Response.json({ error: "Missing config payload." }, { status: 400 });
    }

    const config = parseConfigPayload(configJson);
    if (!config) {
        return Response.json({ error: "Invalid config payload." }, { status: 400 });
    }

    const configItems: ConfigItem[] = [];
    for (const [key, value] of Object.entries<string>(config)) {
        configItems.push({
            configName: key,
            configValue: value
        })
    }

    // update the config items
    try {
        const isUpdated = await backendClient.updateConfig(configItems);
        if (!isUpdated) {
            return Response.json(
                { error: "Backend rejected settings update." },
                { status: 502 });
        }
    } catch (error) {
        return Response.json(
            { error: error instanceof Error ? error.message : "Failed to update config." },
            { status: 502 });
    }

    return { config: config }
}

function parseConfigPayload(configJson: string): Record<string, string> | null {
    try {
        const parsed = JSON.parse(configJson);
        if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) return null;

        const config: Record<string, string> = {};
        for (const [key, value] of Object.entries(parsed)) {
            if (typeof value !== "string") return null;
            config[key] = value;
        }

        return config;
    } catch {
        return null;
    }
}
