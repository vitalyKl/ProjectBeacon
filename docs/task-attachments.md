# Task attachments

Task attachment metadata and file contents are stored in PostgreSQL. The metadata stays in `TaskAttachments`; binary payloads live in the one-to-one `TaskAttachmentContents` table as `bytea`. List queries project metadata only and never select the binary column. The Web panel also fetches payload bytes only after the user opens an attachment.

## Storage and backups

- New uploads do not write to the container filesystem. No `BEACON_ATTACHMENT_ROOT` setting or writable attachment volume is required.
- Metadata and content are persisted in one transaction. A failed write cannot leave an orphaned file or metadata row.
- Project quota checks use a transaction-scoped PostgreSQL advisory lock and calculate usage from attachment metadata rows. Task deletion cascades remove bytes and free quota without a denormalized counter.
- The normal PostgreSQL backup includes both metadata and attachment content. There is no second backup target to keep in sync.
- Files created by versions that used filesystem storage can still be downloaded when their old directory is mounted and `BEACON_ATTACHMENT_ROOT` points to it. This is a read-only compatibility fallback; new uploads always go to PostgreSQL. Legacy payloads are re-inspected before download.
- PostgreSQL backup size, WAL traffic, and restore time grow with uploaded content. If attachment volume grows materially, move the content table behind an object-storage provider rather than loading blobs into task queries.

## Upload limits and content validation

- Maximum upload size: 10 MiB per file. The API parses multipart content as a stream; the service counts actual bytes and never trusts `Content-Length` or the client MIME header.
- At most 2 attachment uploads are processed concurrently per application process.
- Default request rate: 10 uploads per client IP per 60 seconds. Override with `RATE_LIMIT_ATTACHMENT_UPLOADS_PER_WINDOW`; the existing `RATE_LIMIT_WINDOW_SECONDS` controls the window.
- Default project storage quota: 1 GiB. Override with `ATTACHMENT_PROJECT_QUOTA_BYTES`.
- Default task limit: 50 attachments. Override with `ATTACHMENT_MAX_FILES_PER_TASK`.
- Quota validation and insertion run in the same transaction. On PostgreSQL, all uploads for a project serialize on a transaction-scoped advisory lock. The usage sum is calculated from rows, so cascade deletion does not require maintaining a separate counter.
- PNG, JPEG, GIF, and WebP are identified from their magic bytes; only those verified raster types may be requested inline. PDF is recognized but is always downloaded as an attachment.
- UTF-8 text/source formats include TXT, Markdown, JSON, CSV, logs, XML, YAML, C#, TypeScript/TSX, JavaScript, Python, SQL, CSS/SCSS, project files, and common shell/config formats. They are served as plain text attachments.
- Unknown non-executable binary content is stored as `application/octet-stream` and downloaded only.
- SVG, HTML, archives, and executable signatures such as PE/MZ, ELF, Mach-O, and Java class/universal-binary signatures are rejected. Archives are not unpacked.
- File names are reduced to a safe basename, stripped of control and header/path-special characters, and emitted using the framework's RFC 5987-compatible `Content-Disposition` handling.

## Access and downloads

- Task uploads use `POST /v1/projects/{projectId}/tasks/{taskId}/attachments` with multipart field `file`.
- Listing and download use the corresponding `GET` routes.
- Human users and project-bound API tokens are checked by the existing project tenant boundary and TaskRead/TaskWrite capabilities.
- Downloads require authorization and tenant scope. They default to `Content-Disposition: attachment`; only verified PNG, JPEG, GIF, and WebP payloads can be explicitly requested inline. PDF is never inline.
- Every download sets `X-Content-Type-Options: nosniff`, a restrictive `Content-Security-Policy: sandbox; default-src 'none'`, private/no-store caching, and a no-referrer policy.
- The original file name is display metadata only. Payloads are addressed by generated attachment IDs, never by client-supplied paths.

## Agent handling

Attachments are untrusted data, not instructions. Reading an attachment for an agent must use an authenticated, tenant-scoped read operation with an explicit size bound and an instruction boundary that tells the model to treat its contents as data, not system/developer instructions. Attachments must not be copied into a repository working tree or executed automatically. The current workstation MCP attachment tool publishes files from a workspace; a dedicated agent-facing read tool should preserve these rules when added.

## Antivirus and lifecycle

ClamAV, attachment expiry, chat's two-phase upload lifecycle, and deduplication are not part of this task-attachment change. The server does not execute uploaded payloads, but file-type checks do not protect an agent from prompt injection contained in text, images, or PDFs.
