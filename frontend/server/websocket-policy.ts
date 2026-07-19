export const MAX_FRONTEND_TOPIC_MESSAGE_BYTES = 4 * 1024;
export const MAX_FRONTEND_TOPIC_KEY_BYTES = 64;
export const MAX_FRONTEND_TOPIC_MODE_BYTES = 8;

export const FRONTEND_TOPIC_MODES = Object.freeze({
  ctp: "state",
  cxs: "state",
  ha: "event",
  hp: "event",
  hr: "event",
  hs: "event",
  qa: "event",
  qp: "state",
  qr: "event",
  qs: "state",
  uftbmp: "state",
} as const);

export const MAX_FRONTEND_TOPICS = Object.keys(FRONTEND_TOPIC_MODES).length;

export const FRONTEND_WEBSOCKET_SERVER_OPTIONS = Object.freeze({
  noServer: true,
  maxPayload: 16 * 1024,
  maxFragments: 16,
  maxBufferedChunks: 32,
});
