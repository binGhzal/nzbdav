import fs from "node:fs";

const FatalOutput = "frontend_startup_failure code=startup_failed\n";

let reported = false;
const terminate = (): never => {
  if (!reported) {
    reported = true;
    try {
      fs.writeSync(process.stderr.fd, FatalOutput);
    } catch {
      // There is no safe secondary output channel after stderr fails.
    }
  }
  process.exit(1);
};

process.env.DEBUG = "";
process.once("uncaughtException", terminate);
process.once("unhandledRejection", terminate);

try {
  await import("./server.js");
} catch {
  terminate();
}
