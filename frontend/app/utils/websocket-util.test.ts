import { afterEach, describe, expect, it, vi } from "vitest";
import { receiveMessage } from "./websocket-util";

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
