# Unity Tripo Gateway

Secure provider gateway for AI 3D Studio. It keeps the Tripo API key outside Unity assets, authenticates local clients, enforces consent/budget/idempotency, persists asynchronous jobs, and downloads successful outputs immediately into a hashed artifact vault.

This process never imports candidates into Unity. Phase 2's Unity client separately verifies the authenticated vault root and hashes before an explicit quarantine/preview/accept flow. MCP workflow tools remain a Phase 3 gate in `Documentation~/tripo-ai-integration-spec.md`.

## Configuration

Required:

```powershell
$env:TRIPO_GATEWAY_TOKEN = '<long-random-local-token>'
$env:TRIPO_GATEWAY_UPLOAD_ROOTS = 'D:\ApprovedInput;F:\ApprovedArt'
```

On Windows, start the gateway and use **Window → AI 3D Studio (Unity MCP) → Configuração → Credencial Tripo** to save the API key in Windows Credential Manager. The provider starts using it immediately without a restart. Unity never persists the key itself.

`TRIPO_API_KEY` remains an optional environment fallback, and is the required method on platforms where a secure credential store is not available:

```powershell
$env:TRIPO_API_KEY = '<tripo-api-key>'
```

Optional:

| Variable | Default | Purpose |
|---|---:|---|
| `TRIPO_GATEWAY_DATA_DIR` | Local application data | Durable jobs and artifact vault |
| `TRIPO_API_BASE_URL` | `https://openapi.tripo3d.ai/v3` | Official global API |
| `TRIPO_GATEWAY_MAX_CREDITS_PER_JOB` | `1000` | Local per-job cap |
| `TRIPO_GATEWAY_MAX_DOWNLOAD_BYTES` | `1073741824` | Per-artifact stream limit |
| `TRIPO_GATEWAY_PORT` | `8771` | Loopback HTTP port |

Custom API hosts are rejected unless `TRIPO_GATEWAY_ALLOW_CUSTOM_BASE_URL=1`. The launcher refuses non-loopback bind addresses.

## Run

```powershell
python -m pip install -r TripoGateway~/requirements.txt
python TripoGateway~/main.py
```

Health is public and secret-free at `GET http://127.0.0.1:8771/health`. Every `/v1/*` route requires `Authorization: Bearer <TRIPO_GATEWAY_TOKEN>`.

Credential routes are authenticated and never return the key:

- `GET /v1/credentials/tripo`: safe status/source metadata;
- `PUT /v1/credentials/tripo`: replace the secure key using `{ "apiKey": "..." }`;
- `DELETE /v1/credentials/tripo`: remove the persisted key; an environment fallback, if present, becomes active again.

Creating a remote task additionally requires:

- `Idempotency-Key` header;
- `disclosure.external_send_approved: true`;
- an explicit estimate and credit cap;
- sufficient account balance.

Cancellation after Tripo submission is local/cooperative: polling continues for factual reconciliation, artifacts are not downloaded, and the gateway does not claim a provider refund.

## Test

```powershell
python -m pip install -r TripoGateway~/requirements-dev.txt
python -m pytest TripoGateway~/tests
```

The automated suite uses a fake provider and HTTP fixtures only. It never consumes real credits. A real `GET /account/balance` smoke is opt-in and must not print the API key.
