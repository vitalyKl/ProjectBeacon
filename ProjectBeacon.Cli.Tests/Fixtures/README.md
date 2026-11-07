# Protocol fixtures

These sanitized fixtures exercise the workstation's supported external model-server contract:

- `LlamaServer/health-loading.json` and `health-ready.json` cover llama-server readiness states.
- `LlamaServer/v1-models.json` covers the model-list response used by the local backend adapter.

Update them from anonymized responses when the supported llama-server protocol changes. Native agent provider streaming is covered by dedicated provider tests.
