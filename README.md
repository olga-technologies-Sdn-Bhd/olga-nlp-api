# OLGA Connect NLP API

.NET 10 API for intent normalization, versioned embeddings, eligibility-aware candidate retrieval, reciprocal ranking, deterministic explanations, feedback, and model evaluation.

## Service boundary

OLGA uses two logical APIs that may share one Azure deployment and Azure Database for PostgreSQL Flexible Server during the MVP. Schema and API ownership remain separate.

The Core Product API owns authentication integration, members, profiles, profile visibility, consent, events, event registration, Live Mode, presence, connections, blocks, chat, files, notifications, privacy workflows, moderation, and administration.

The NLP API owns:

- `nlp.NlpIntent` normalization metadata and lifecycle;
- `nlp.NlpEmbedding`, model versions, ranking versions, and processing jobs;
- idempotent match requests, ranked results, explanations, feedback, suppression, and evaluation;
- bounded candidate retrieval through approved read-only projections.

The NLP API must not update Core Product API tables or infer permission from a score. Production eligibility is read from `nlp.vw_member_context_eligibility` and `nlp.vw_member_relationship`, which the database implementation derives from authoritative IAM, profile, consent, event, Live Mode, connection, and block state. Local InMemory fixtures use stand-alone tables behind the same repository interface.

Cross-API effects use the transactional `ops.OutboxEvent`. NLP emits `NlpIntentNormalized.v1`, `NlpIntentMatchReady.v1`, `NlpMatchRequestCompleted.v1`, `NlpFeedbackRecorded.v1`, and `NlpEvaluationRunCompleted.v1` with minimal metadata and no raw intent, feedback, or evaluation sample text. The Core Product API/its workers consume those events to drive notifications, mobile projections, analytics, privacy, or other product workflows.

## Local development

The Development profile uses EF Core InMemory, deterministic fake embeddings, inline embedding processing, and seeded members. It requires no Azure subscription or PostgreSQL server.

The pull-request and environment deployment process is documented in [CI/CD operations](docs/CI_CD.md).

```powershell
dotnet restore Olga.Nlp.sln
dotnet test Olga.Nlp.sln
dotnet run --project src/Olga.Nlp.Api
```

Create or replace an intent. An existing intent requires the `If-Match` value returned by `GET /v1/intents/{id}`.

```powershell
curl.exe -X POST http://localhost:5000/v1/intents `
  -H "Content-Type: application/json" `
  -H "X-Member-Id: A123" `
  -d '{"intent_id":"new-want","context_id":"event-001","intent_type":"WANT","text":"Need cold-chain storage","expires_at":"2030-01-01T00:00:00Z","category":"cold-chain storage","industry":"pharmaceutical","geography":"Selangor","language":"en"}'
```

Create an idempotent match request:

```powershell
curl.exe -X POST http://localhost:5000/v1/match-requests `
  -H "Content-Type: application/json" `
  -H "X-Member-Id: A123" `
  -H "Idempotency-Key: req-1001" `
  -d '{"request_id":"req-1001","intent_id":"a-want","context_id":"event-001","limit":7}'
```

`B456` should be returned as an explained reciprocal match. `D111` is blocked and must never be returned. Poll or retrieve the reproducible execution with `GET /v1/match-requests/req-1001`.

## Processing model

Normalization is synchronous. The intent row commits original text, PII-minimized normalized text, normalized hash, language, PII signal, preprocessing version, and `PROCESSING` status together.

Local development uses the deterministic 1,536-dimensional fake provider with `EmbeddingProcessing:Mode=Inline`. Deployed environments use Azure OpenAI with managed identity and `Queued`, which writes `nlp.NlpProcessingJob`; the worker embeds the stored normalized text, applies bounded retries and a lease, and marks the intent `MATCH_READY`. Normal searches reuse stored vectors and never call the embedding provider.

All WANT and OFFER vectors in a comparison must use the same active model version and 1,536 dimensions. Candidate retrieval is bounded to at most 200 eligible candidates before .NET ranking. PostgreSQL persists embeddings as native `vector(1536)` values; application ranking reuses those stored vectors.

## API contracts

All endpoints are anonymous for the initial MVP. Member-scoped endpoints use the optional `X-Member-Id` header and otherwise fall back to `Mvp__DefaultMemberId` (`A123` by default). Do not treat this selector as authentication; restore verified caller and evaluator identities before exposing sensitive data beyond the controlled MVP environment. Member IDs are limited to 64 characters and are supplied by the identity lifecycle rather than invented by clients.

- `POST /v1/intents`
- `GET /v1/intents/{intentId}`
- `POST /v1/match-requests`
- `GET /v1/match-requests/{requestId}`
- `POST /v1/matches/{matchResultId}/feedback`
- `POST /v1/matches/search` - compatibility endpoint
- `POST /v1/feedback` - compatibility endpoint
- `POST /v1/matches/score-pair` - internal evaluation only
- `POST /v1/normalize` - internal development/evaluation only
- `POST /v1/internal/evaluation-runs` - approved de-identified datasets only
- `GET /v1/internal/evaluation-runs/{evaluationRunId}`
- `GET /health`
- `GET /ready`

The development deployment uses external HTTPS ingress. Swagger is available at `https://ca-olga-nlp-api-dev.agreeableocean-8bb4ca77.malaysiawest.azurecontainerapps.io/swagger`. External ingress exposes the entire API, not only Swagger; all endpoints remain unauthenticated during the temporary MVP phase.

Swagger displays each applicable client header:

| Header | Applies to | Client behavior |
| --- | --- | --- |
| `X-Member-Id` | Intent, match-request, search, and feedback operations | Optional only because the MVP falls back to `Mvp__DefaultMemberId`; maximum 64 characters. Replace this selector with a validated JWT identity before production use. |
| `Idempotency-Key` | Stateful `POST` operations | Required, maximum 128 characters. Generate a UUID for each new logical action and reuse it for retries. For match requests and the legacy search endpoint, it must equal `request_id`. |
| `If-Match` | `POST /v1/intents` | Send the ETag returned by `GET /v1/intents/{intentId}` when updating an existing intent; omit it only when creating the intent. |

Errors contain `code`, `message`, and `correlation_id`; unhandled errors return the root exception message in every environment, and `Diagnostics__IncludeExceptionDetails` controls whether they also include `stack_trace`. Foreign-key failures return `RESOURCE_REFERENCE_NOT_FOUND` instead of an unhandled database error. Responses never expose vectors, raw identity subjects, member presence cells, block direction, provider payloads, or moderation detail.

The current middleware requires `Idempotency-Key` consistently, but durable same-key/same-result replay is complete only for match requests and evaluation runs. Intent and feedback replay storage remains production-readiness work. The internal evaluation endpoints are reachable through the external API ingress despite their `/internal/` path and remain unauthenticated during the temporary MVP phase; workload identity and evaluator authorization are required before production use.

## Match execution and feedback

`nlp.MatchRequest` stores a canonical request hash, actor, intent/context, language/options snapshot, status, preprocessing/model/ranking versions, threshold, candidate count, and completion state. Reusing a request ID with different inputs returns `IDEMPOTENCY_KEY_REUSED`; an exact replay returns the existing status/results.

Each returned result carries its persisted `match_result_id`, 1-based rank, semantic score, reciprocal score when available, final score, label, deterministic reason codes, and all applied versions. Eligibility and active suppression are checked before ranking; a score cannot bypass them.

Feedback labels are controlled: `USEFUL`, `NOT_USEFUL`, or `INAPPROPRIATE`. Corrections append a new row referencing the immediately previous feedback. They never overwrite history. Free-text feedback is length limited and rejected when contact-style PII is detected.

Evaluation runs are anonymous during the initial MVP. They still read only `APPROVED` datasets and `TEST` samples, persist the exact model/ranking versions, and return aggregate metrics rather than sample text or private report locations.

## Production configuration

- Configure `ConnectionStrings__PostgreSql` for the PgBouncer endpoint and set `EmbeddingProcessing__Mode=Queued`.
- Configure `AzureOpenAI__Endpoint`, `AzureOpenAI__DeploymentName`, and `AzureOpenAI__ModelVersion`; set each workload's user-assigned identity through `AzureOpenAI__ManagedIdentityClientId` or `AZURE_CLIENT_ID`. The model version must match the active `nlp.nlp_model_version` database record.
- Restore the approved workload-identity mechanism before expanding access beyond the controlled MVP environment.
- Keep API/worker/migration database identities separate and grant least privilege by schema/function.
- Keep intent text, vectors, identity values, presence, provider payloads, and feedback text out of telemetry.
- Use private endpoints, Key Vault, Application Insights/OpenTelemetry, retry/dead-letter monitoring, and the approved retention/privacy workflows.

The database migrations are intentionally maintained separately. They must implement the v2.3 `nlp` tables and approved projections represented by the EF mappings in this repository.
