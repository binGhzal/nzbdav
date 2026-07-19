import type { Route } from "./+types/route";
import { backendClient, type ConfigItem } from "~/clients/backend-client.server";
import {
    createPublicFailureResponse,
    projectPublicFailureIdentityHeaders,
} from "~/utils/public-failure";

export function headers({ actionHeaders }: Route.HeadersArgs) {
    return projectPublicFailureIdentityHeaders(actionHeaders);
}

export async function action({ request }: Route.ActionArgs) {
    // get the ConfigItems to update
    const formData = await request.formData();
    const configJson = formData.get("config");
    if (typeof configJson !== "string" || configJson.trim() === "") {
        return createPublicFailureResponse(request, 400, "invalid_request");
    }

    const config = parseConfigPayload(configJson);
    if (!config) {
        return createPublicFailureResponse(request, 400, "invalid_request");
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
            return createPublicFailureResponse(request, 502, "upstream_unavailable");
        }
    } catch {
        return createPublicFailureResponse(request, 502, "upstream_unavailable");
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
