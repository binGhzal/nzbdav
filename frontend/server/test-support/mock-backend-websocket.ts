export type MockBackendWebsocket = {
  once(event: "message", listener: () => void): unknown;
  send(data: string): unknown;
};

const currentUsenetConnectionState = {
  Topic: "cxs",
  Message: "0|4|2|4|10|2",
};

export function attachMockBackendWebsocket(socket: MockBackendWebsocket) {
  // The real backend authenticates the first frame, then publishes its cached
  // state topics. Mirror that order so the frontend bridge can cache `cxs`.
  socket.once("message", () => {
    socket.send(JSON.stringify(currentUsenetConnectionState));
  });
}
