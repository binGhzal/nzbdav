import { EventEmitter } from "node:events";
import { describe, expect, it, vi } from "vitest";
import { attachMockBackendWebsocket } from "./test-support/mock-backend-websocket";

describe("attachMockBackendWebsocket", () => {
  it("publishes the current Usenet connection state after the authentication frame", () => {
    const socket = new FakeSocket();
    attachMockBackendWebsocket(socket);

    expect(socket.send).not.toHaveBeenCalled();

    socket.emit("message", Buffer.from("e2e", "utf8"));

    expect(socket.send).toHaveBeenCalledOnce();
    expect(socket.send).toHaveBeenCalledWith(JSON.stringify({
      Topic: "cxs",
      Message: "0|4|2|4|10|2",
    }));

    socket.emit("message", Buffer.from("later-subscription", "utf8"));

    expect(socket.send).toHaveBeenCalledOnce();
  });
});

class FakeSocket extends EventEmitter {
  send = vi.fn();
}
