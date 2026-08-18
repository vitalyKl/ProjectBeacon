import type { ApprovalRecord, TokenRecord } from "./types.js";

export function presentApiToken(token: TokenRecord, secret?: string) {
  return {
    id: token.id,
    project_id: token.projectId,
    name: token.name,
    prefix: token.prefix,
    scopes: token.scopes,
    created_by: token.createdBy,
    last_used_at: token.lastUsedAt ? token.lastUsedAt.toISOString() : null,
    expires_at: token.expiresAt ? token.expiresAt.toISOString() : null,
    revoked_at: token.revokedAt ? token.revokedAt.toISOString() : null,
    created_at: token.createdAt.toISOString(),
    ...(secret !== undefined ? { token: secret } : {}),
  };
}

export function presentApproval(approval: ApprovalRecord) {
  return {
    id: approval.id,
    project_id: approval.projectId,
    session_id: approval.sessionId,
    action: approval.action,
    payload: approval.payload,
    status: approval.status,
    requested_at: approval.requestedAt.toISOString(),
    resolved_at: approval.resolvedAt ? approval.resolvedAt.toISOString() : null,
    resolved_by: approval.resolvedBy,
  };
}
