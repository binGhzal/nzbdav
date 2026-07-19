import { afterEach, describe, expect, it, vi } from "vitest";
import { handleError, handleStreamingError } from "./entry.server";

describe("server framework error output", () => {
  afterEach(() => vi.restoreAllMocks());

  it("emits only fixed events for hostile request and streaming errors", () => {
    const hostile = "credential-marker|http://user:pass@backend\r\n\u001b[31m";
    const output: string[] = [];
    vi.spyOn(console, "error").mockImplementation((...values: unknown[]) => {
      output.push(values.join(" "));
    });

    handleError(new Error(hostile));
    handleStreamingError(new Error(hostile));

    expect(output).toEqual(["frontend_ssr_error", "frontend_ssr_stream_error"]);
    expect(output.join("|")).not.toContain(hostile);
  });
});
