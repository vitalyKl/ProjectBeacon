# @beacon/db

Drizzle schema and the initial v1 SQL dump. IDs are UUIDv7 generated in app code.

## Migrate

Requires `DATABASE_URL` pointing at Postgres 16.

```bash
DATABASE_URL=postgres://beacon:PASSWORD@127.0.0.1:5432/beacon pnpm --filter @beacon/db db:migrate
```

Compose does not publish Postgres by default. Either publish `5432` locally or run the same command from a container that can reach the `postgres` service.

## Scripts

- `pnpm --filter @beacon/db drizzle:generate` — generate a follow-up migration from schema changes
- `pnpm --filter @beacon/db db:migrate` — apply migrations (`drizzle:migrate` is an alias)
