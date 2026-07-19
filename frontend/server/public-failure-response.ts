import type express from "express";
import {
  createPublicFailureEnvelope,
  type PublicFailureCode,
  type PublicFailureEnvelope,
} from "../app/utils/public-failure.js";

const FixedTerminalLog = "frontend_http_failure code=internal_error";

type FailureRequest = Readonly<{ method?: string }>;

export type SerializedPublicFailure = Readonly<{
  body: string;
  envelope: PublicFailureEnvelope;
}>;

export function serializePublicFailure(code: PublicFailureCode): SerializedPublicFailure {
  const envelope = createPublicFailureEnvelope(code);
  return Object.freeze({ body: JSON.stringify(envelope), envelope });
}

export function sendExpressPublicFailure(
  request: FailureRequest,
  response: express.Response,
  status: number,
  code: PublicFailureCode,
  options: Readonly<{
    allow?: string;
    resetHeaders?: boolean;
  }> = {},
): void {
  if (response.headersSent) {
    response.destroy();
    return;
  }
  if (options.resetHeaders) {
    for (const name of response.getHeaderNames()) response.removeHeader(name);
  }

  const { body, envelope } = serializePublicFailure(code);
  response.status(status);
  response.setHeader("Content-Type", "application/json; charset=utf-8");
  response.setHeader("X-Correlation-ID", envelope.correlation_id);
  response.setHeader("X-Error-Code", envelope.code);
  response.setHeader("Content-Length", String(Buffer.byteLength(body, "utf8")));
  if (options.allow !== undefined) response.setHeader("Allow", options.allow);
  response.end(request.method === "HEAD" ? undefined : body);
}

export function handleExpressTerminalFailure(
  _error: unknown,
  request: express.Request,
  response: express.Response,
  _next: express.NextFunction,
): void {
  console.error(FixedTerminalLog);
  if (response.headersSent) {
    response.destroy();
    return;
  }
  sendExpressPublicFailure(request, response, 500, "internal_error", {
    resetHeaders: true,
  });
}
