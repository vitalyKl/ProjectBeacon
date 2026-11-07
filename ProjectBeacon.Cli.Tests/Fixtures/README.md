# Protocol fixtures

These fixtures are kept with tests so changes in our external HTTP assumptions are visible in code review.

- `OpenCode/v1-global-health.json` follows the documented v1 `GET /global/health` response.
- `OpenCode/v2-info.json` and the v1/v2 session creation wrappers preserve the response shapes already used by the v1/v2 adapters and their integration tests.
- `OpenCode/v1-messages.json` is the assistant message/part sample used to exercise text, reasoning, and tool parsing.
- `LlamaServer/health-ready.json` and `health-loading.json` follow the upstream llama.cpp `GET /health` examples: HTTP 200 with `status: ok`, and HTTP 503 while loading.
- `LlamaServer/v1-models.json` is a minimal OpenAI-compatible list response for future model-list parsing tests.

These are sanitized contract fixtures, not a claim that every file was captured from a production user's process. Update them from anonymized live responses when the supported external runtime versions change.
