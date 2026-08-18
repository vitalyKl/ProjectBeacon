export const PROJECT_TOKEN_PREFIX = "bcn_";
export const PROJECT_TOKEN_LENGTH = 47;

const BASE64URL_BODY = /^[A-Za-z0-9_-]+$/;

export function isProjectTokenFormat(token: string): boolean {
  if (!token.startsWith(PROJECT_TOKEN_PREFIX) || token.length !== PROJECT_TOKEN_LENGTH) {
    return false;
  }
  return BASE64URL_BODY.test(token.slice(PROJECT_TOKEN_PREFIX.length));
}
