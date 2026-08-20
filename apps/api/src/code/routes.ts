import type { IncomingMessage } from "node:http";
import type { Duplex } from "node:stream";

import { WebSocketServer } from "ws";

import type { AuthDeps } from "../auth/routes.js";
import type { RepoStore } from "../repos/store.js";
import type { TokenStore } from "../tokens/store.js";
import { isSidecarTunnelEnabled } from "./flags.js";
import { SIDECAR_TUNNEL_PATH, type SidecarTunnelHub } from "./tunnel.js";
import { attachSidecarTunnelSocket } from "./ws.js";

function requestUrl(req: IncomingMessage): URL {
  return new URL(req.url ?? "/", `http://${req.headers.host ?? "127.0.0.1"}`);
}

function incomingToRequest(req: IncomingMessage): Request {
  const headers = new Headers();
  for (const [key, value] of Object.entries(req.headers)) {
    if (value === undefined) {
      continue;
    }
    if (Array.isArray(value)) {
      for (const item of value) {
        headers.append(key, item);
      }
      continue;
    }
    headers.set(key, value);
  }
  return new Request(requestUrl(req), { method: req.method, headers });
}

export function attachSidecarTunnelUpgrade(
  server: {
    on(
      event: "upgrade",
      listener: (req: IncomingMessage, socket: Duplex, head: Buffer) => void,
    ): unknown;
  },
  deps: AuthDeps & { store: TokenStore & RepoStore },
  hub: SidecarTunnelHub,
  enabled: () => boolean = isSidecarTunnelEnabled,
): WebSocketServer {
  const wss = new WebSocketServer({ noServer: true });
  server.on("upgrade", (req, socket, head) => {
    const path = requestUrl(req).pathname;
    if (path !== SIDECAR_TUNNEL_PATH) {
      return;
    }
    if (!enabled()) {
      socket.write("HTTP/1.1 404 Not Found\r\nConnection: close\r\n\r\n");
      socket.destroy();
      return;
    }
    wss.handleUpgrade(req, socket, head, (ws) => {
      attachSidecarTunnelSocket({
        ws,
        request: incomingToRequest(req),
        deps,
        hub,
      });
    });
  });
  return wss;
}
