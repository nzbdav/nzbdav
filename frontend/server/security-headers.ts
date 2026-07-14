import type { NextFunction, Request, Response } from "express";
import { shouldProxyToBackend } from "./proxy-path.js";

/**
 * Defensive UI response headers. Skipped on proxied WebDAV/API paths so
 * non-browser clients are not affected. CSP is deferred (report-only later).
 */
export function securityHeadersMiddleware(req: Request, res: Response, next: NextFunction) {
  if (shouldProxyToBackend(req.method, req.path || "")) {
    next();
    return;
  }

  res.setHeader("X-Content-Type-Options", "nosniff");
  res.setHeader("Referrer-Policy", "same-origin");
  res.setHeader("X-Frame-Options", "SAMEORIGIN");
  next();
}
