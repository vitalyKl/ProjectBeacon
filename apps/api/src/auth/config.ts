export type AuthConfig = {
  bootstrapAdminToken: string | undefined;
  workerToken: string | undefined;
  authLocal: boolean;
  authLocalInviteOnly: boolean;
  authGithub: boolean;
  githubClientId: string | undefined;
  githubClientSecret: string | undefined;
  secureCookies: boolean;
  trustProxy: boolean;
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
    secureCookies: env["NODE_ENV"] === "production",
    trustProxy: env["TRUST_PROXY"] === "true",
  };
}

export function isGithubOAuthEnabled(config: AuthConfig): boolean {
  return Boolean(config.authGithub && config.githubClientId && config.githubClientSecret);
}
