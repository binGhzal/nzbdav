import { describe, expect, it } from "vitest";
import { getWebsocketUrl, withUrlBase } from "./url-base";

describe("URL_BASE helpers", () => {
  it("prefixes server-relative paths with the configured URL base", () => {
    expect(withUrlBase("/api?mode=queue")).toBe("/nzbdav/api?mode=queue");
    expect(withUrlBase("api/test-rclone-connection")).toBe("/nzbdav/api/test-rclone-connection");
  });

  it("builds websocket URLs under the configured URL base", () => {
    expect(getWebsocketUrl()).toBe("wss://media.example.test/nzbdav/ws");
  });
});
