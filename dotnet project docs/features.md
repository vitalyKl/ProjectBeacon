# Feature index

Current behavior, not an implementation plan. Shape and boundaries: `ProjectBeacon-design-doc-v3.md`. Pipeline contract: `task-pipeline-local-agents.md`. MCP tools: `mcp-host.md`.

## Model orchestration

- The model registry belongs to the user account. Backend types are `FreeToken`, `LlamaCpp`, and `OpenAiCompatible`.
- The control plane stores the registry and generates llama-swap `config.yaml`. It does not start or stop the process.
- `beacon client` starts the process. `UseOwnSwapper` uses the in-client swapper. Otherwise the client launches external `llama-swap`.
- `Concurrent` backends are placed in `groups.resident`. The rest stay in the swap group (one active model). Concurrent launch commands must use `${PORT}`.
- Health is the device heartbeat (`probeJson.llamaSwapStatus`), not a Web `IHostedService`. If the client is offline, model orchestration is unavailable and the rest of Beacon keeps running.
- There is no UI for editing `config.yaml` by hand.
- MCP surface: `model_upsert`, `model_delete`, `model_bind`, `model_unbind`, `model_status`, `proxy_reload`, `proxy_unload`.

## Task pipeline

Planner, actor, and review are separate sessions. The actor does not receive the planner transcript. Review sees artifacts, not transcripts. See `task-pipeline-local-agents.md`.

## Explicitly not shipped

- A Beacon-hosted agent process.
- HTTP MCP, hosted clone, outbound WSS, `write_handoff`.
- Learn and Files screens.
- Hand-authored llama-swap YAML as the source of truth.
