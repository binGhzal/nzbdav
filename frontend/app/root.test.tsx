import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

describe("root ErrorBoundary", () => {
  it("renders only fixed text when a route error contains hostile detail", async () => {
    const hostile = "credential-marker|http://backend-secret\r\n\u001b[31m";
    process.env.SESSION_KEY = "a".repeat(64);
    const { ErrorBoundary } = await import("./root");

    render(ErrorBoundary({ error: new Error(hostile) } as never));
    const alert = screen.getByRole("alert");

    expect(alert.textContent).toBe("Request failedThe page could not be loaded. Please try again.");
    expect(alert.textContent).not.toContain(hostile);
  });
});
