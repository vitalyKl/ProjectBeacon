import WebSocket from "ws";

export const SIDECAR_TUNNEL_PATH = "/v1/sidecar";

export type TunnelRpcQuery = Record<
  string,
  string | number | Array<string | undefined> | undefined
>;

export type SidecarTunnelSocket = {
  send(data: string): void;
  close(): void;
  on(event: "open" | "message" | "close" | "error", listener: (payload?: unknown) => void): void;
};

export type SidecarTunnelDialer = (
  url: string,
  headers: Record<string, string>,
) => SidecarTunnelSocket;

export function sidecarTunnelUrl(controlPlaneUrl: string, hostOverride?: string): string {
  if (hostOverride && hostOverride.trim()) {
    const raw = hostOverride.trim();
    if (/^wss?:\/\//i.test(raw)) {
      return `${raw.replace(/\/+$/, "")}${SIDECAR_TUNNEL_PATH}`;
    }
    const prefixed = /^[a-zA-Z][a-zA-Z0-9+.-]*:\/\//.test(raw) ? raw : `https://${raw}`;
    return httpToWs(prefixed);
  }
  return httpToWs(controlPlaneUrl);
}

function httpToWs(raw: string): string {
  const url = new URL(raw);
  url.protocol = url.protocol === "http:" ? "ws:" : "wss:";
  url.pathname = SIDECAR_TUNNEL_PATH;
  url.search = "";
  url.hash = "";
  return url.toString();
}

export function applyQuery(url: URL, query: TunnelRpcQuery | undefined): void {
  if (!query) {
    return;
  }
  for (const [key, value] of Object.entries(query)) {
    if (value === undefined || value === "") {
      continue;
    }
    if (Array.isArray(value)) {
      for (const item of value) {
        if (item !== undefined && item !== "") {
          url.searchParams.append(key, String(item));
        }
      }
      continue;
    }
    url.searchParams.set(key, String(value));
  }
}

export function defaultSidecarTunnelDialer(
  url: string,
  headers: Record<string, string>,
): SidecarTunnelSocket {
  const socket = new WebSocket(url, { headers });
  return {
    send(data) {
      socket.send(data);
    },
    close() {
      socket.close();
    },
    on(event, listener) {
      if (event === "message") {
        socket.on("message", (data) => {
          listener(typeof data === "string" ? data : Buffer.from(data as Buffer).toString("utf8"));
        });
        return;
      }
      socket.on(event, listener);
    },
  };
}
