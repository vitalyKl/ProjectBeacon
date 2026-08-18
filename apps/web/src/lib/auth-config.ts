import { connection } from "next/server";

export type PublicAuthConfig = {
  githubEnabled: boolean;
  githubClientId: string | null;
};

export async function publicAuthConfig(): Promise<PublicAuthConfig> {
  // Request-time: compose sets AUTH_GITHUB / client id / public URL at runtime.
  await connection();
  const clientId = process.env.GITHUB_OAUTH_CLIENT_ID?.trim() || null;
  const githubEnabled = process.env.AUTH_GITHUB === "true" && Boolean(clientId);
  return { githubEnabled, githubClientId: githubEnabled ? clientId : null };
}

export async function publicOrigin(): Promise<string> {
  await connection();
  return (process.env.BEACON_PUBLIC_URL ?? "http://localhost:3000").replace(/\/$/, "");
}

export async function githubCallbackUrl(): Promise<string> {
  return `${await publicOrigin()}/login/github`;
}
