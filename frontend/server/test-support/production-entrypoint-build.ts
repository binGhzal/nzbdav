import type { IncomingMessage } from "node:http";
import type { RequestHandler } from "express";
import type { WebSocketServer } from "ws";

export type ProductionEntrypointFixture = {
  app: RequestHandler;
  authenticateWebsocketUpgrade: (request: IncomingMessage) => Promise<boolean>;
  initializeWebsocketServer: (websocketServer: WebSocketServer) => void;
};

declare global {
  var __PINRAIL_PRODUCTION_ENTRYPOINT_FIXTURE__: ProductionEntrypointFixture | undefined;
}

function currentFixture(): ProductionEntrypointFixture {
  const fixture = globalThis.__PINRAIL_PRODUCTION_ENTRYPOINT_FIXTURE__;
  if (!fixture) {
    throw new Error("Production entrypoint test fixture was not initialized");
  }
  return fixture;
}

export const app: RequestHandler = (req, res, next) => {
  return currentFixture().app(req, res, next);
};

export function authenticateWebsocketUpgrade(request: IncomingMessage): Promise<boolean> {
  return currentFixture().authenticateWebsocketUpgrade(request);
}

export function initializeWebsocketServer(websocketServer: WebSocketServer): void {
  currentFixture().initializeWebsocketServer(websocketServer);
}
