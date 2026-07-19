import { createRequire } from "node:module";

type DebugControl = {
  disable: () => string;
};

export function disableUnsafeRuntimeDebugOutput(): void {
  process.env.DEBUG = "";

  const require = createRequire(import.meta.url);
  const debug = require("debug") as DebugControl;
  debug.disable();
}
