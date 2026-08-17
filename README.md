# ProjectBeacon

Project operating system for mixed human + AI-agent development.

TypeScript monorepo (pnpm workspaces + Turborepo). Application logic is not implemented yet — this is the layout and tooling bootstrap. See [docs/design.md](docs/design.md).

## Prerequisites

- Node.js 22+ or 24 LTS
- [pnpm](https://pnpm.io) 10

## Setup

```bash
pnpm install
pnpm typecheck
```

Workspace scripts: `pnpm build`, `pnpm lint`, `pnpm test`, `pnpm dev`.
