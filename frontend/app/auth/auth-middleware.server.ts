import type express from "express";
import { AUTH_MODE, isAuthenticated } from "~/auth/authentication.server";

const ALWAYS_PUBLIC_PATHS = ["/__manifest"];
const LOCAL_PUBLIC_PATHS = [
  "/login",
  "/login.data",
  "/onboarding",
  "/onboarding.data",
];
const LOCAL_ONLY_PATHS = [...LOCAL_PUBLIC_PATHS, "/logout"];

// URL_BASE is read at runtime — the Express server mounts middleware under this prefix,
// so within this middleware `req.path` is already stripped. But `res.redirect("/login")`
// emits an absolute path back to the browser, which the browser interprets relative to
// the origin, not the URL_BASE mount. So we have to put the prefix back on outgoing
// Location values manually. Mirror of the normalizer in `server.ts`.
function normalizeUrlBase(raw: string | undefined): string {
  if (!raw) return "";
  const trimmed = raw.trim();
  if (trimmed === "" || trimmed === "/") return "";
  const withLeading = trimmed.startsWith("/") ? trimmed : `/${trimmed}`;
  return withLeading.replace(/\/+$/, "");
}
const URL_BASE = normalizeUrlBase(process.env.URL_BASE);

export async function authMiddleware(
  req: express.Request,
  res: express.Response,
  next: express.NextFunction,
): Promise<void> {
  const pathname = decodeURIComponent(req.path);
  if (AUTH_MODE === "authentik-proxy" && LOCAL_ONLY_PATHS.includes(pathname)) {
    res.sendStatus(404);
    return;
  }
  if (ALWAYS_PUBLIC_PATHS.includes(pathname)) return next();
  if (AUTH_MODE === "local" && LOCAL_PUBLIC_PATHS.includes(pathname)) return next();

  // Allow authenticated sessions
  if (await isAuthenticated(req)) return next();

  if (AUTH_MODE === "authentik-proxy") {
    res.sendStatus(401);
    return;
  }

  res.redirect(302, `${URL_BASE}/login`);
}
