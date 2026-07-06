import { afterEach, describe, expect, it, vi } from "vitest";
import { createReconnectingWebSocket, createReconnectBackoff, parseTopicMessage, receiveMessage } from "./websocket-util";

describe("parseTopicMessage", () => {
  it("returns a topic message for valid envelopes", () => {
    expect(parseTopicMessage(JSON.stringify({ Topic: "qs", Message: "paused" }))).toEqual({
      topic: "qs",
      message: "paused",
    });
  });

  it("returns null for invalid JSON and malformed envelopes", () => {
    expect(parseTopicMessage("{")).toBeNull();
    expect(parseTopicMessage(JSON.stringify({ Topic: "qs", Message: { text: "paused" } }))).toBeNull();
    expect(parseTopicMessage(JSON.stringify({ Topic: "", Message: "paused" }))).toBeNull();
  });
});

describe("receiveMessage", () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("delivers valid topic messages", () => {
    const onMessage = vi.fn();

    receiveMessage(onMessage)(
      new MessageEvent("message", {
        data: JSON.stringify({ Topic: "qs", Message: "paused" }),
      }),
    );

    expect(onMessage).toHaveBeenCalledWith("qs", "paused");
  });

  it("ignores invalid JSON and malformed envelopes", () => {
    const onMessage = vi.fn();
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    const handler = receiveMessage(onMessage);

    expect(() => handler(new MessageEvent("message", { data: "{" }))).not.toThrow();
    expect(() =>
      handler(
        new MessageEvent("message", {
          data: JSON.stringify({ Topic: "qs", Message: { text: "paused" } }),
        }),
      ),
    ).not.toThrow();

    expect(onMessage).not.toHaveBeenCalled();
    expect(warn).toHaveBeenCalledTimes(2);
  });
});

describe("createReconnectBackoff", () => {
  it("backs off exponentially and caps at the max delay", () => {
    const backoff = createReconnectBackoff({ initialDelayMs: 100, maxDelayMs: 500 });

    expect(backoff.nextDelayMs()).toBe(100);
    expect(backoff.nextDelayMs()).toBe(200);
    expect(backoff.nextDelayMs()).toBe(400);
    expect(backoff.nextDelayMs()).toBe(500);
    expect(backoff.nextDelayMs()).toBe(500);
  });

  it("resets after a successful connection", () => {
    const backoff = createReconnectBackoff({ initialDelayMs: 100, maxDelayMs: 500 });

    backoff.nextDelayMs();
    backoff.nextDelayMs();
    backoff.reset();

    expect(backoff.nextDelayMs()).toBe(100);
  });
});

describe("createReconnectingWebSocket", () => {
  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it("ignores stale socket events after reconnecting", () => {
    vi.useFakeTimers();
    const sockets: FakeSocket[] = [];
    const onMessage = vi.fn();
    const onClose = vi.fn();

    const dispose = createReconnectingWebSocket({
      createSocket: () => {
        const socket = new FakeSocket();
        sockets.push(socket);
        return socket as unknown as WebSocket;
      },
      onMessage,
      onOpen: (socket) => socket.send("subscribe"),
      onClose,
      backoff: createReconnectBackoff({ initialDelayMs: 100, maxDelayMs: 100 }),
    });

    const first = sockets[0];
    first.onclose?.(new CloseEvent("close"));
    expect(onClose).toHaveBeenCalledTimes(1);
    vi.advanceTimersByTime(100);
    expect(sockets).toHaveLength(2);

    const second = sockets[1];
    first.onmessage?.(new MessageEvent("message", {
      data: JSON.stringify({ Topic: "qs", Message: "stale" }),
    }));
    first.onerror?.(new Event("error"));
    first.onopen?.(new Event("open"));
    first.onclose?.(new CloseEvent("close"));

    expect(onMessage).not.toHaveBeenCalled();
    expect(second.close).not.toHaveBeenCalled();
    expect(first.send).not.toHaveBeenCalled();
    expect(onClose).toHaveBeenCalledTimes(1);

    second.onopen?.(new Event("open"));
    expect(second.send).toHaveBeenCalledWith("subscribe");
    second.onmessage?.(new MessageEvent("message", {
      data: JSON.stringify({ Topic: "qs", Message: "fresh" }),
    }));
    expect(onMessage).toHaveBeenCalledWith("qs", "fresh");

    dispose();
  });
});

class FakeSocket {
  onmessage: ((event: MessageEvent) => void) | null = null;
  onopen: ((event: Event) => void) | null = null;
  onclose: ((event: CloseEvent) => void) | null = null;
  onerror: ((event: Event) => void) | null = null;
  send = vi.fn();
  close = vi.fn();
}
