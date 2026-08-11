# Unity AI 3D Gateway

Secure multi-provider gateway for AI 3D Studio. It keeps Tripo and Meshy API keys outside Unity assets, authenticates local clients, enforces consent/budget/idempotency, persists asynchronous jobs, and downloads successful outputs immediately into a hashed artifact vault.

This process never imports candidates into Unity. The Unity client separately verifies the authenticated vault root and hashes before an explicit quarantine/preview/accept flow. The same durable workflow contract covers generation, Smart Retopology, segmentation/completion, rig-check/rig and animation retargeting.

## Configuration

Required:

```powershell
$env:TRIPO_GATEWAY_TOKEN = '<long-random-local-token>'
$env:TRIPO_GATEWAY_UPLOAD_ROOTS = 'D:\ApprovedInput;F:\ApprovedArt'
```

On Windows, start the gateway and use **Window → AI 3D Studio (Unity MCP) → Configuração** to save each provider key in its own Windows Credential Manager entry. The provider starts using it immediately without a restart. Unity never persists either key itself.

`TRIPO_API_KEY` and `MESHY_API_KEY` remain optional environment fallbacks, and are required on platforms where a secure credential store is not available:

```powershell
$env:TRIPO_API_KEY = '<tripo-api-key>'
$env:MESHY_API_KEY = '<meshy-api-key>'
```

Optional:

| Variable | Default | Purpose |
|---|---:|---|
| `TRIPO_GATEWAY_DATA_DIR` | Local application data | Durable jobs and artifact vault |
| `TRIPO_API_BASE_URL` | `https://openapi.tripo3d.ai/v3` | Official global API |
| `MESHY_API_BASE_URL` | `https://api.meshy.ai` | Official Meshy API |
| `TRIPO_GATEWAY_MAX_CREDITS_PER_JOB` | `1000` | Local per-job cap |
| `TRIPO_GATEWAY_MAX_DAILY_ESTIMATED_CREDITS` | `1000` | Daily estimate quota per project (`0` disables) |
| `TRIPO_GATEWAY_MAX_ACTIVE_JOBS_PER_PROJECT` | `3` | Concurrent provider jobs per project |
| `TRIPO_GATEWAY_MAX_DOWNLOAD_BYTES` | `1073741824` | Per-artifact stream limit |
| `TRIPO_GATEWAY_PORT` | `8771` | Loopback HTTP port |
| `TRIPO_WEBHOOK_SECRET` | unset | Tripo signing secret (`whsec_…`) for `/v1/webhooks/tripo` |
| `TRIPO_WEBHOOK_TOLERANCE_SECONDS` | `300` | Signature replay window |

Custom API hosts are rejected unless `TRIPO_GATEWAY_ALLOW_CUSTOM_BASE_URL=1`. The launcher refuses non-loopback bind addresses.

## Run

```powershell
python -m pip install -r TripoGateway~/requirements.txt
python TripoGateway~/main.py
```

Health is public and secret-free at `GET http://127.0.0.1:8771/health`. Every local `/v1/*` route requires `Authorization: Bearer <TRIPO_GATEWAY_TOKEN>`, except the provider-facing `/v1/webhooks/tripo`, which requires the official `Tripo-Webhook-Signature` HMAC and delivery/event headers.

Credential routes are authenticated and never return the key. Replace `{provider}` with `tripo` or `meshy`:

- `GET /v1/credentials/{provider}`: safe status/source metadata;
- `PUT /v1/credentials/{provider}`: replace the secure key using `{ "apiKey": "..." }`;
- `DELETE /v1/credentials/{provider}`: remove the persisted key; an environment fallback, if present, becomes active again.

Job and upload bodies carry `provider: "tripo" | "meshy"`. Meshy supports text/image/multi-image generation, refine texturing, convert, remesh, resize, UV unwrap, humanoid rigging and animation. Semantic segment/complete remains Tripo-only. The Unity/MCP planner also accepts `provider: "auto"`, selecting Meshy except for that Tripo-only workflow.

Creating a remote task additionally requires:

- `Idempotency-Key` header;
- `disclosure.external_send_approved: true`;
- an explicit estimate and credit cap;
- sufficient account balance.

Cancellation after provider submission is local/cooperative: polling continues for factual reconciliation, artifacts are not downloaded, and the gateway does not claim a provider refund.

Operational routes:

- `GET /v1/telemetry/summary`: counts, operations and credit totals without prompts, file content or credentials;
- `POST /v1/webhooks/tripo`: signed `task.completed`, `task.failed` and `balance.low` deliveries, deduplicated by delivery id;
- `GET /v1/capabilities`: exposes only safe quota/webhook configuration metadata.

Configure the public HTTPS webhook URL in Tripo Settings and keep the signing secret only in the gateway environment. Invalid/stale signatures are rejected, duplicate deliveries are acknowledged idempotently, and failed reconciliation remains retryable.

## Test

```powershell
python -m pip install -r TripoGateway~/requirements-dev.txt
python -m pytest TripoGateway~/tests
```

The automated suite uses fake providers and HTTP fixtures only. It never consumes real credits. A real balance or generation smoke is opt-in and must not print either API key.
