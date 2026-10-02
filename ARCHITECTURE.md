# OLGA Connect NLP API Architecture and Data Flow

## Purpose and boundary

The NLP API converts member WANT and OFFER text into versioned, explainable, eligibility-aware match results. It owns normalization metadata, embeddings, model/ranking versions, processing jobs, match requests/results, feedback, suppression, and controlled evaluation datasets/runs.

It does not own member identity, profiles, field visibility, consent, event registration, Live Mode, presence, connections, blocks, chat, files, notifications, privacy workflows, moderation, or administration. Those are authoritative in the separate `D:\OLGA\Projects\Olga.Core` repository. An NLP score can never create eligibility, reveal a profile, establish a connection, or authorize chat.

## Solution projects

| Project | Responsibility | Dependency direction |
| --- | --- | --- |
| `Olga.Nlp.Domain` | NLP domain records, execution states, scores, ranking configuration, feedback, and evaluation models. | None |
| `Olga.Nlp.Contracts` | Public/internal HTTP request, response, version, reason, feedback, evaluation, and error contracts. | None |
| `Olga.Nlp.Application` | Intent processing, idempotent match execution, reciprocal ranking orchestration, feedback, and evaluation use cases. | Domain, Contracts |
| `Olga.Nlp.Infrastructure` | EF Core persistence, repositories, deterministic local normalizer/embedding, ranking helpers, and the Azure embedding seam. | Application, Domain |
| `Olga.Nlp.Api` | Minimal API routes, request identity, correlation/error envelope, OpenAPI, health/readiness, and local seed data. | Application, Contracts, Infrastructure |
| `Olga.Nlp.Worker` | Queued embedding job polling, leases, retries, and terminal state changes. | Infrastructure |
| `Olga.Nlp.UnitTests` | Normalization, reciprocal scoring, ranking, and explanation behavior. | Infrastructure |
| `Olga.Nlp.IntegrationTests` | Intent, eligibility, matching, feedback, idempotency, and persistence flows. | API/Application/Infrastructure |
| `Olga.Nlp.ContractTests` | HTTP contract, identity, error, and endpoint behavior. | API |

## Intent processing flow

```text
Core/member client -> POST /v1/intents
  -> derive actor identity; never trust a body member ID
  -> validate intent type, context, text, expiry, and ETag on replacement
  -> normalize and mask contact-style PII synchronously
  -> persist original text, normalized text, hash, language,
     preprocessing version, and PROCESSING state
  -> write NlpIntentNormalized.v1 to ops.OutboxEvent
  -> Inline mode embeds immediately; Queued mode creates NlpProcessingJob
  -> persist vector + model version and mark MATCH_READY
```

The normalized hash plus preprocessing/model versions make embedding reuse and reprocessing traceable. Normal match searches reuse stored vectors and do not call the embedding provider.

## Match request flow

```text
Caller -> POST /v1/match-requests + Idempotency-Key
  -> canonical request hash and ownership check
  -> exact replay returns existing execution; changed input conflicts
  -> load requester's MATCH_READY WANT and optional OFFER
  -> query at most 200 candidates through Core-approved eligibility
  -> exclude inactive, expired, blocked, connected, incompatible-vector,
     or suppressed candidates before ranking
  -> compare requester WANT to candidate OFFER
  -> optionally compare candidate WANT to requester OFFER
  -> harmonic reciprocal score + versioned weighted features
  -> deterministic reason codes; suppress results without a safe reason
  -> persist request versions, threshold, candidate count, ranked results
  -> write NlpMatchRequestCompleted.v1
  -> return only the requested top 3-7; never return vectors or policy internals
```

In the integrated database, `nlp.vw_member_context_eligibility` and `nlp.vw_member_relationship` must be read-only projections derived from Core-owned IAM, consent, event, social, and moderation state. Stand-alone local tables are test fixtures behind the same repository interface, not production authorities.

## Feedback and evaluation flow

Feedback is attached to a persisted match result owned by the authenticated requester. Labels are controlled (`USEFUL`, `NOT_USEFUL`, `INAPPROPRIATE`). Corrections append a row that references the immediately superseded feedback; history is never overwritten. Free text is bounded and rejected when contact-style PII is detected.

Evaluation endpoints are anonymous during the initial MVP. Runs still use only approved datasets and TEST samples, bind exact model/ranking versions, store aggregate metrics, and emit `NlpEvaluationRunCompleted.v1` without exposing sample text or private report paths.

## Cross-API data flow

```text
Core -> authoritative consent/event/social state
  -> read-only eligibility and relationship projections
  -> NLP filters candidates
  -> NLP produces versioned ranked results
  -> minimal NlpMatchRequestCompleted.v1 outbox event
  -> Core notification and sync workflows
  -> authenticated member sees an authorization-filtered match projection
```

Other NLP events are `NlpIntentNormalized.v1`, `NlpIntentMatchReady.v1`, `NlpFeedbackRecorded.v1`, and `NlpEvaluationRunCompleted.v1`. Payloads must contain IDs, statuses, counts, and versions only—never raw intent, feedback, vectors, identity values, or evaluation sample text.

## Security and operational rules

- During the anonymous MVP, member context comes from optional `X-Member-Id` and otherwise uses `Mvp__DefaultMemberId`; this is not authentication. Production member identity must come from a validated `sub` claim.
- Internal evaluation routes currently rely on internal-only ingress. Service-to-service calls require workload identity and evaluator authorization before production use.
- Stateful POST operations require a client-generated `Idempotency-Key`; retries of one logical action reuse the key. Existing intent updates use the last returned ETag in `If-Match`. Swagger documents these headers on applicable operations.
- The NLP runtime receives read-only access to approved Core projections and write access only to the `nlp` and permitted `ops` objects.
- One model space and vector dimension must be used for both directions in a reciprocal comparison.
- Consent, eligibility, block, suppression, and authorization failures fail closed.
- Telemetry excludes intent text, vectors, presence, relationship direction, provider payloads, and feedback text.
- Workers require bounded retries, leases, dead-letter monitoring, and idempotent state transitions.

## PostgreSQL v2.4 integration

Relational matching uses `nlp.get_requester_intent` and `nlp.get_eligible_candidates` through typed Npgsql parameters. Eligibility and suppression therefore bound the candidate set to 50–200 rows before PostgreSQL applies exact cosine distance. Match results and feedback are persisted through `nlp.save_match_results` and `nlp.save_feedback`; no ANN index is used.

JSON documents map to `jsonb`, embeddings map to `vector(1536)`, and mutable configuration/model/request rows use trigger-generated `bigint row_version`. Stateful `/v1` POST operations require a bounded `Idempotency-Key`. Because the current candidate function selects the latest active requester WANT, the API fails closed when a caller asks to match an older WANT rather than selecting candidates with one embedding and ranking them with another.

## Current readiness

The repository currently passes 4 unit, 7 integration, and 3 contract tests. Production gaps remain: configure OIDC/JWT authentication and authorization, implement the Azure embedding provider, complete workload identity and transport publishing, and add production observability/load/failure evidence. The cross-solution assessment and release gates are in `D:\OLGA\Projects\Olga.Core\docs\SENIOR_ARCHITECT_REVIEW.md`.
