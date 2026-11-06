# Task attachments

Task attachments are stored as opaque files outside the web root. The database stores only metadata and a generated storage key; the original filename is never used as a storage path.

## Storage

Set `BEACON_ATTACHMENT_ROOT` on every API and Web instance to the same persistent storage location. The default `App_Data/attachments` directory under the application base directory is suitable for local development only.

Container deployments must mount a persistent volume at the configured path. Backups must include both the attachment volume and the database; restoring only one side leaves metadata or file content missing.

## Limits and access

- Maximum upload size: 10 MiB per file.
- Task uploads use `POST /v1/projects/{projectId}/tasks/{taskId}/attachments` with multipart field `file`.
- Listing and download use the corresponding `GET` routes.
- Human users and project-bound API tokens are checked by the existing project tenant boundary and TaskRead/TaskWrite capabilities.
- Downloads default to attachment disposition. Only PNG, JPEG, GIF, and WebP can be requested inline.
- Storage names are generated identifiers; client-provided filenames are display metadata only.

## Agent publishing

The workstation MCP server exposes `task_attach_file`. It accepts a workspace-relative path and optional task ID (otherwise it uses `BEACON_TASK_ID`). It rejects paths outside the workspace, reparse points/symbolic links, missing files, and files over 10 MiB.
