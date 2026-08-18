export type AuthConfig = {
  bootstrapAdminToken: string | undefined;
  workerToken?: string;
  authLocal: boolean;
  authLocalInviteOnly: boolean;
  authGithub: boolean;
  githubClientId: string | undefined;
  githubClientSecret: string | undefined;
  githubAppId?: string;
  githubAppPrivateKey?: string;
  githubAppWebhookSecret?: string;
  secureCookies: boolean;
  trustProxy: boolean;
  workerToken: string | undefined;
  indexRpcUrl: string;
  indexRpcToken: string | undefined;
};

export function loadAuthConfig(env: NodeJS.ProcessEnv = process.env): AuthConfig {
  return {
    bootstrapAdminToken: env["BOOTSTRAP_ADMIN_TOKEN"],
    workerToken: env["BEACON_WORKER_TOKEN"],
    authLocal: env["AUTH_LOCAL"] === "true",
    authLocalInviteOnly: env["AUTH_LOCAL_INVITE_ONLY"] !== "false",
    authGithub: env["AUTH_GITHUB"] === "true",
    githubClientId: env["GITHUB_OAUTH_CLIENT_ID"],
    githubClientSecret: env["GITHUB_OAUTH_CLIENT_SECRET"],
    githubAppId: env["GITHUB_APP_ID"],
    githubAppPrivateKey: env["GITHUB_APP_PRIVATE_KEY"],
    githubAppWebhookSecret: env["GITHUB_APP_WEBHOOK_SECRET"],
    secureCookies: env["NODE_ENV"] === "production",
    trustProxy: env["TRUST_PROXY"] === "true",
    workerToken: env["BEACON_WORKER_TOKEN"],
    indexRpcUrl: (env["INDEX_RPC_URL"] ?? "http://worker:7744").replace(/\/+$/, ""),
    indexRpcToken: env["INDEX_RPC_TOKEN"],
  };
}

export function isGithubAppConfigured(config: AuthConfig): boolean {
  return Boolean(config.githubAppId && config.githubAppPrivateKey);
}

export function isGithubOAuthEnabled(config: AuthConfig): boolean {
  return Boolean(config.authGithub && config.githubClientId && config.githubClientSecret);
}
