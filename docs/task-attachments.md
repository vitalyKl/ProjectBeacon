# Task attachments

Task attachment metadata and file contents are stored in PostgreSQL. The metadata stays in `TaskAttachments`; binary payloads live in the one-to-one `TaskAttachmentContents` table as `bytea`. The list endpoint projects metadata only and never selects the binary column.

## Storage and backups

- New uploads do not write to the container filesystem. No `BEACON_ATTACHMENT_ROOT` setting or writable attachment volume is required.
- Metadata and content are persisted by one EF Core `SaveChangesAsync` transaction. A failed write cannot leave an orphaned file or metadata row.
- The normal PostgreSQL backup includes both metadata and attachment content. There is no second backup target to keep in sync.
- Files created by versions that used filesystem storage can still be downloaded when their old directory is mounted and `BEACON_ATTACHMENT_ROOT` points to it. This is a read-only compatibility fallback; new uploads always go to PostgreSQL. Keep the old volume backed up until legacy files have been migrated or retired.

PostgreSQL backup size, WAL traffic, and restore time grow with uploaded content. The current per-file limit is 10 MiB; if attachment volume grows materially, move the content table behind an object-storage provider rather than loading blobs into task queries.

## Limits and access

- Maximum upload size: 10 MiB per file.
- Task uploads use `POST /v1/projects/{projectId}/tasks/{taskId}/attachments` with multipart field `file`.
- Listing and download use the corresponding `GET` routes.
- Human users and project-bound API tokens are checked by the existing project tenant boundary and TaskRead/TaskWrite capabilities.
- Downloads default to attachment disposition. Only PNG, JPEG, GIF, and WebP can be requested inline.
- The original filename is display metadata only. File payloads are addressed by generated attachment IDs, never by client-supplied paths.

## Agent publishing

The workstation MCP server exposes `task_attach_file`. It accepts a workspace-relative path and optional task ID (otherwise it uses `BEACON_TASK_ID`). It rejects paths outside the workspace, reparse points/symbolic links, missing files, and files over 10 MiB.
