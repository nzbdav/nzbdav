import "react-router";
import { createRequestHandler } from "@react-router/express";
import express from "express";
import { ipKeyGenerator, rateLimit } from "express-rate-limit";
import { createProxyMiddleware } from "http-proxy-middleware";
import { websocketServer } from "./websocket.server";
import { shouldProxyToBackend } from "./proxy-path";
import { logger } from "./logger";
import { isAuthenticated } from "~/auth/authentication.server";
import { authMiddleware } from "~/auth/auth-middleware.server";

export const app = express();
export const initializeWebsocketServer = websocketServer.initialize;

// Proxy all webdav and api requests to the backend
const forwardToBackend = createProxyMiddleware({
  target: process.env.BACKEND_URL,
  changeOrigin: true,
  on: {
    error: (error, req, res) => {
      logger.error(
        `Backend proxy failed for ${req.method ?? "UNKNOWN"} ${req.url ?? "unknown URL"}`,
        error,
      );
      if ("writeHead" in res && !res.headersSent) {
        res.writeHead(502, { "Content-Type": "text/plain" });
        res.end("Bad Gateway");
      }
    },
    proxyRes: (proxyRes, req, res) => {
      proxyRes.on('close', () => {
        if (!res.writableEnded) {
          res.end();
        }
      });
    },
  },
});

const setApiKeyForAuthenticatedRequests = async (req: express.Request) => {
  // if the path is not /api, do nothing
  if (!req.path.startsWith("/api")) return;
  const apikey = req.query.apikey || req.query.apiKey || req.headers["x-api-key"];
  const hasApiKey = apikey && typeof apikey === "string";

  // if the request already has an apikey, do nothing
  if (hasApiKey) return;

  // if the request is not authenticated, do nothing
  const authenticated = await isAuthenticated(req);
  if (!authenticated) return;

  // otherwise, set the api key header
  req.headers["x-api-key"] = process.env.FRONTEND_BACKEND_API_KEY || "";
}

const credentialPaths = new Set([
  "/login",
  "/login.data",
  "/onboarding",
  "/onboarding.data",
]);
const credentialRateLimiter = rateLimit({
  windowMs: 15 * 60 * 1000,
  limit: 20,
  standardHeaders: true,
  legacyHeaders: false,
  keyGenerator: (req) => {
    const remoteAddress = req.socket.remoteAddress;
    return remoteAddress ? ipKeyGenerator(remoteAddress) : "unknown";
  },
  skip: (req) => {
    return req.method.toUpperCase() !== "POST"
      || !credentialPaths.has(decodeURIComponent(req.path));
  },
  handler: (req, res, _next, options) => {
    logger.warn(
      `Credential rate limit exceeded for ${req.ip ?? "unknown IP"} on ${req.path}`,
    );
    res.status(options.statusCode).send(options.message);
  },
});

app.use(async (req, res, next) => {
  if (shouldProxyToBackend(req.method, req.path)) {
    await setApiKeyForAuthenticatedRequests(req);
    return forwardToBackend(req, res, next);
  }
  next();
});

// Limit credential attempts without throttling WebDAV, API, or regular UI traffic.
app.use(credentialRateLimiter);

// Require authentication for all React Router routes
app.use(authMiddleware);

// Let frontend handle all other requests
app.use(
  createRequestHandler({
    build: () => import("virtual:react-router/server-build"),
  }),
);
