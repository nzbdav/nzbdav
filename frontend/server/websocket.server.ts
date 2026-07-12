import WebSocket, { WebSocketServer } from 'ws';
import { isAuthenticated } from "../app/auth/authentication.server";
import type { IncomingMessage } from 'http';
import { logger } from "./logger";

function initializeWebsocketServer(wss: WebSocketServer) {
    // keep track of socket subscriptions
    const websockets = new Map<WebSocket, any>();
    const subscriptions = new Map<string, Set<WebSocket>>();
    const lastMessage = new Map<string, string>();
    initializeWebsocketClient(subscriptions, lastMessage);

    // authenticate new websocket sessions
    wss.on("connection", async (ws: WebSocket, request: IncomingMessage) => {
        try {
            // ensure user is logged in
            if (!await isAuthenticated(request)) {
                logger.debug(`Rejected unauthenticated websocket connection from ${request.socket.remoteAddress ?? "unknown IP"}`);
                ws.close(1008, "Unauthorized");
                return;
            }

            // handle topic subscription
            ws.onmessage = (event: WebSocket.MessageEvent) => {
                try {
                    const topics = JSON.parse(event.data.toString());
                    websockets.set(ws, topics);
                    for (const topic in topics) {
                        const topicSubscriptions = subscriptions.get(topic);
                        if (topicSubscriptions) topicSubscriptions.add(ws);
                        else subscriptions.set(topic, new Set<WebSocket>([ws]));
                        if (topics[topic] === 'state') {
                            const messageToSend = lastMessage.get(topic);
                            if (messageToSend) ws.send(messageToSend);
                        }
                    }
                } catch {
                    ws.close(1003, "Could not process topic subscription. If recently updated, try refreshing the page.");
                }
            };

            // unsubscribe from topics
            ws.onclose = () => {
                const topics = websockets.get(ws);
                if (topics) {
                    websockets.delete(ws);
                    for (const topic in topics) {
                        const topicSubscriptions = subscriptions.get(topic);
                        if (topicSubscriptions) topicSubscriptions.delete(ws);
                    }
                }
            };
        } catch (error) {
            logger.error("Error authenticating websocket session", error);
            ws.close(1011, "Internal server error");
            return;
        }
    });
}

export function initializeWebsocketClient(subscriptions: Map<string, Set<WebSocket>>, lastMessage: Map<string, string>) {
    let reconnectRetryDelay = 1000;
    let reconnectTimeout: NodeJS.Timeout | null = null;
    let connected = false;
    let connectionFailures = 0;
    let lastFailureLogAt = 0;
    const url = getBackendWebsocketUrl();

    function logConnectionFailure(message: string, error?: unknown) {
        const now = Date.now();
        connectionFailures += 1;
        if (connectionFailures === 1 || now - lastFailureLogAt >= 60_000) {
            logger.warn(`${message}; retrying in ${reconnectRetryDelay} ms`, error);
            lastFailureLogAt = now;
        }
    }

    function connect() {
        const socket = new WebSocket(url);

        socket.on('error', (error: Error) => {
            if (!connected) {
                logConnectionFailure(`Could not connect to backend websocket at ${url}`, error);
            } else {
                logger.warn("Backend websocket error", error);
            }
        });

        socket.onopen = () => {
            const reconnected = connectionFailures > 0;
            connected = true;
            connectionFailures = 0;
            lastFailureLogAt = 0;
            logger.info(reconnected ? "Backend websocket reconnected" : "Backend websocket connected");
            if (reconnectTimeout) {
                clearTimeout(reconnectTimeout);
                reconnectTimeout = null;
            }

            socket.send(Buffer.from(process.env.FRONTEND_BACKEND_API_KEY!, "utf-8"), { binary: false });
        };

        socket.onmessage = (event: WebSocket.MessageEvent) => {
            try {
                const rawMessage = event.data.toString();
                const topicMessage: unknown = JSON.parse(rawMessage);
                if (!topicMessage || typeof topicMessage !== "object") return;

                const { Topic: topic, Message: message } = topicMessage as Record<string, unknown>;
                if (typeof topic !== "string" || typeof message !== "string") return;

                lastMessage.set(topic, rawMessage);
                const subscribed = subscriptions.get(topic) || [];
                subscribed.forEach(client => {
                    if (client.readyState === client.OPEN) {
                        client.send(rawMessage);
                    }
                });
            } catch (error) {
                logger.error("Ignoring malformed backend websocket message", error);
            }
        };

        socket.onclose = (event: WebSocket.CloseEvent) => {
            if (connected) {
                connected = false;
                logConnectionFailure(
                    `Backend websocket closed (code ${event.code}, reason: ${event.reason || "none"})`,
                );
            } else if (connectionFailures === 0) {
                logConnectionFailure(`Could not connect to backend websocket at ${url}`);
            }
            disconnectBrowserClients(subscriptions, lastMessage);
            scheduleReconnect();
        };
    }

    function scheduleReconnect() {
        if (reconnectTimeout) clearTimeout(reconnectTimeout);

        reconnectTimeout = setTimeout(() => {
            connect();
        }, reconnectRetryDelay);
    }

    connect();
}

export function disconnectBrowserClients(
    subscriptions: Map<string, Set<WebSocket>>,
    lastMessage: Map<string, string>,
) {
    lastMessage.clear();
    const clients = new Set<WebSocket>();
    subscriptions.forEach(topicClients => topicClients.forEach(client => clients.add(client)));
    clients.forEach(client => {
        if (client.readyState === WebSocket.OPEN) {
            client.close(1012, "Backend websocket reconnecting");
        }
    });
}

function getBackendWebsocketUrl() {
    const host = process.env.BACKEND_URL!;
    return `${host.replace(/\/$/, '')}/ws`.replace(/^http/, 'ws');
}

export const websocketServer = {
    initialize: initializeWebsocketServer
}