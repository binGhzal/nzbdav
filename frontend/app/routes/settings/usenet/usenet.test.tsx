import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { UsenetSettings } from "./usenet";

const REDACTED_SECRET = "__NZBDAV_REDACTED__";

describe("UsenetSettings", () => {
    beforeEach(() => {
        FakeWebSocket.instances.length = 0;
    });

    afterEach(() => {
        cleanup();
        vi.restoreAllMocks();
        vi.unstubAllGlobals();
    });

    it("allows saving non-secret edits when the provider password is redacted and unchanged", () => {
        vi.stubGlobal("WebSocket", FakeWebSocket);
        const setNewConfig = vi.fn();
        const providerConfig = {
            Providers: [
                {
                    Type: 1,
                    Host: "news.example.invalid",
                    Port: 563,
                    UseSsl: true,
                    User: "user",
                    Pass: REDACTED_SECRET,
                    MaxConnections: 50,
                    Priority: 100,
                    StatPipeliningEnabled: true,
                },
            ],
        };
        const config = {
            "usenet.providers": JSON.stringify(providerConfig),
            "usenet.nntp-pipelining.enabled": "true",
        };

        render(<UsenetSettings config={config} setNewConfig={setNewConfig} />);

        fireEvent.click(screen.getByTitle("Edit Provider"));
        fireEvent.change(screen.getByLabelText("Max Connections"), { target: { value: "80" } });

        const saveButton = screen.getByRole("button", { name: "Save Provider" });
        expect((saveButton as HTMLButtonElement).disabled).toBe(false);

        fireEvent.click(saveButton);

        expect(setNewConfig).toHaveBeenCalledWith(expect.objectContaining({
            "usenet.providers": expect.stringContaining(REDACTED_SECRET),
        }));
    });

    it("does not update live connection rows while editing a provider", () => {
        vi.stubGlobal("WebSocket", FakeWebSocket);
        const providerConfig = createProviderConfig();
        const config = {
            "usenet.providers": JSON.stringify(providerConfig),
            "usenet.nntp-pipelining.enabled": "true",
        };

        const { container } = render(<UsenetSettings config={config} setNewConfig={vi.fn()} />);
        fireEvent.click(screen.getByTitle("Edit Provider"));

        act(() => {
            FakeWebSocket.instances[0].onmessage?.(new MessageEvent("message", {
                data: JSON.stringify({ Topic: "cxs", Message: "0|3|1|0|1|0" }),
            }));
        });

        expect(container.querySelector('[class*="connection-bar"]')).toBeNull();
    });

    it("ignores malformed live connection messages", () => {
        vi.stubGlobal("WebSocket", FakeWebSocket);
        const config = {
            "usenet.providers": JSON.stringify(createProviderConfig()),
            "usenet.nntp-pipelining.enabled": "true",
        };

        const { container } = render(<UsenetSettings config={config} setNewConfig={vi.fn()} />);

        act(() => {
            FakeWebSocket.instances[0].onmessage?.(new MessageEvent("message", {
                data: JSON.stringify({ Topic: "cxs", Message: "0|bad|1|0|1|0" }),
            }));
        });

        expect(container.querySelector('[class*="connection-bar"]')).toBeNull();
    });

    it("rejects an arbitrary backend error body when a provider connection test fails", async () => {
        vi.stubGlobal("WebSocket", FakeWebSocket);
        vi.stubGlobal("fetch", vi.fn(async () => new Response("provider auth failed", { status: 401 })));
        const config = {
            "usenet.providers": JSON.stringify(createProviderConfig()),
            "usenet.nntp-pipelining.enabled": "true",
        };

        render(<UsenetSettings config={config} setNewConfig={vi.fn()} />);

        fireEvent.click(screen.getByTitle("Edit Provider"));
        fireEvent.change(screen.getByLabelText("Username"), { target: { value: "changed-user" } });
        fireEvent.click(screen.getByRole("button", { name: "Test Connection" }));

        await waitFor(() => {
            expect(screen.getByRole("alert").textContent)
                .toBe("Connection test failed: HTTP 401");
        });
    });

    it("renders a stable connection-timeout compatibility failure", async () => {
        vi.stubGlobal("WebSocket", FakeWebSocket);
        vi.stubGlobal("fetch", vi.fn(async () => Response.json({
            status: true, connected: false, error: "hostile-legacy-detail",
            code: "connection_timeout", correlation_id: "0123456789abcdef0123456789abcdef",
        }, {
            status: 504,
            headers: { "X-Error-Code": "connection_timeout", "X-Correlation-ID": "0123456789abcdef0123456789abcdef" },
        })));
        const config = { "usenet.providers": JSON.stringify(createProviderConfig()), "usenet.nntp-pipelining.enabled": "true" };

        render(<UsenetSettings config={config} setNewConfig={vi.fn()} />);
        fireEvent.click(screen.getByTitle("Edit Provider"));
        fireEvent.change(screen.getByLabelText("Username"), { target: { value: "changed-user" } });
        fireEvent.click(screen.getByRole("button", { name: "Test Connection" }));

        await waitFor(() => expect(screen.getByRole("alert").textContent).toBe(
            "Connection test failed: The connection test timed out. (0123456789abcdef0123456789abcdef)"));
    });

    it("submits the saved-password marker with the exact TLS and username identity", async () => {
        vi.stubGlobal("WebSocket", FakeWebSocket);
        let submittedHost: FormDataEntryValue | null = null;
        let submittedPort: FormDataEntryValue | null = null;
        let submittedUseSsl: FormDataEntryValue | null = null;
        let submittedUser: FormDataEntryValue | null = null;
        let submittedPassword: FormDataEntryValue | null = null;
        const fetchMock = vi.fn(async (_url: string, init: RequestInit) => {
            const form = init.body as FormData;
            submittedHost = form.get("host");
            submittedPort = form.get("port");
            submittedUseSsl = form.get("use-ssl");
            submittedUser = form.get("user");
            submittedPassword = form.get("pass");
            return Response.json({ status: true, connected: true });
        });
        vi.stubGlobal("fetch", fetchMock);
        const config = {
            "usenet.providers": JSON.stringify(createProviderConfig()),
            "usenet.nntp-pipelining.enabled": "true",
        };

        render(<UsenetSettings config={config} setNewConfig={vi.fn()} />);
        fireEvent.click(screen.getByTitle("Edit Provider"));
        fireEvent.change(screen.getByLabelText("Host"), { target: { value: "NEWS.EXAMPLE.INVALID" } });
        fireEvent.click(screen.getByRole("button", { name: "Test Connection" }));

        await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));
        expect(submittedHost).toBe("NEWS.EXAMPLE.INVALID");
        expect(submittedPort).toBe("563");
        expect(submittedUseSsl).toBe("true");
        expect(submittedUser).toBe("user");
        expect(submittedPassword).toBe(REDACTED_SECRET);
    });
});

function createProviderConfig() {
    return {
        Providers: [
            {
                Type: 1,
                Host: "news.example.invalid",
                Port: 563,
                UseSsl: true,
                User: "user",
                Pass: REDACTED_SECRET,
                MaxConnections: 50,
                Priority: 100,
                StatPipeliningEnabled: true,
            },
        ],
    };
}

class FakeWebSocket {
    static instances: FakeWebSocket[] = [];
    onmessage: ((event: MessageEvent) => void) | null = null;
    onopen: ((event: Event) => void) | null = null;
    onclose: ((event: CloseEvent) => void) | null = null;
    onerror: ((event: Event) => void) | null = null;

    send = vi.fn();
    close = vi.fn();

    constructor() {
        FakeWebSocket.instances.push(this);
    }
}
