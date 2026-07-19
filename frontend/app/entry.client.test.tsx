import type { ReactElement } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

const hydrateRootMock = vi.hoisted(() => vi.fn());

vi.mock("react-dom/client", () => ({ hydrateRoot: hydrateRootMock }));

import { handleClientFrameworkError } from "./entry.client";

describe("client framework error output", () => {
  afterEach(() => vi.restoreAllMocks());

  it("wires every router and React hydration callback to one fixed event", () => {
    const call = hydrateRootMock.mock.calls[0];
    expect(call).toBeDefined();
    const rendered = call[1] as ReactElement<{ children: ReactElement<{
      onError?: (error: unknown, details?: unknown) => void;
    }> }>;
    const options = call[2] as {
      onCaughtError?: (error: unknown, details?: unknown) => void;
      onRecoverableError?: (error: unknown, details?: unknown) => void;
      onUncaughtError?: (error: unknown, details?: unknown) => void;
    };
    const callbacks = [
      rendered.props.children.props.onError,
      options.onCaughtError,
      options.onRecoverableError,
      options.onUncaughtError,
    ];
    expect(callbacks.every(callback => callback === handleClientFrameworkError)).toBe(true);

    const hostile = "credential-marker|http://user:pass@backend\r\n\u001b[31m";
    const output: string[] = [];
    vi.spyOn(console, "error").mockImplementation((...values: unknown[]) => {
      output.push(values.join(" "));
    });
    for (const callback of callbacks) callback?.(new Error(hostile));

    expect(output).toEqual(Array.from({ length: 4 }, () => "frontend_render_error"));
    expect(output.join("|")).not.toContain(hostile);
  });
});
