# Architecture

| Key | Constraint |
|---|---|
| Release topology | Azure-first, Malaysia-aligned single region; separate development, test, and production environments/resource groups/databases/identities/secrets |
| Application style | Schema-separated modular monolith; one authoritative PostgreSQL database per environment; API-only data access |
| Deployables | Product API; asynchronous worker/jobs; separately deployable NLP API behind a stable REST contract; mobile app; admin web app |
| Domain boundaries | `core`, `iam`, `consent`, `event`, `social`, `chat`, `storage`, `notification`, `nlp`, `moderation`, `ops`, `analytics`, `admin` |
| Cross-domain access | Owning service writes; other domains use service APIs or approved read-only views |
| Consistency | Business change + outbox event + sync change committed atomically where applicable |
| Capability gate | Recommendation never grants visibility, connection, chat, file, or notification rights |
| Context boundary | Discovery restricted to shared context, active visibility, active presence when required, and unexpired intent |
| Expiry | Enforced on every read/write; delayed cleanup cannot reactivate expired data |
| Scale evolution | No microservices, AKS, Redis, dedicated vector database, or AI Search until measured ownership/scale limits justify them |
| Delivery target | QA-ready Azure test environment: `2026-10-15` |

# Mobile

| Key | Constraint |
|---|---|
| Stack | React Native + Expo development builds + TypeScript + Expo Router |
| Platforms | iOS and Android from one shared codebase |
| Local database | Encrypted SQLite for bounded read projections, matches, last `sync_sequence`, and pending commands |
| Secure storage | OS keystore / Expo SecureStore for tokens and keys only |
| Device integrations | Expo Notifications; foreground Location; APNs; FCM |
| Offline UX | Render cached state immediately; show offline state and last-sync time; no indefinite spinner or empty-state substitution |
| Offline writes | Persist idempotency key, operation, payload version, timestamp, attempts, and sync state |
| Local purge | Purge context data at expiry; purge projections/commands/cursors on logout or device revocation |
| Sensitive cache | Do not retain presence, moderation, or admin data beyond immediate UX need |
| Accessibility | WCAG 2.2 AA intent: contrast, screen-reader labels, focus order, dynamic text, non-color status cues |
| Localization | Externalized strings; locale-aware dates/geography; English Release 1; multilingual intent processing phased |
| Event resilience | Test throttled 3G, captive portal, airplane mode, duplicate replay, expiry, and hundreds of seeded members |

# Admin Web

| Key | Constraint |
|---|---|
| Stack | React + Vite |
| Hosting | Azure Static Web Apps Free for MVP/internal use; no free-tier SLA |
| Authentication | Workforce/admin identity; MFA or step-up required for privileged endpoints |
| Data access | API only; no direct PostgreSQL, Blob Storage, or Service Bus access |
| Read boundary | Redacted `admin.vw_MemberReview`; no direct access to application tables |
| Functions | Member verification/suspension/anonymization; events; moderation; reports; policy/config; manual matches; NLP evaluation; dashboards; audit |
| Audit | Sensitive reads/writes, exports, role changes, moderation, configuration activation, and denied privileged attempts emit `ops.AuditEvent` |
| Requirements | `FR-03` approve/suspend/anonymize with audit; `FR-18` events/verification/reports/content/consent support; `FR-20` de-identified NLP evaluation export/metrics |

# Backend

| Key | Constraint |
|---|---|
| Runtime | .NET 10; ASP.NET Core 10 Minimal APIs |
| Data access | EF Core + Npgsql default; parameterized Npgsql for pgvector/PostgreSQL-specific operations; Dapper only after profiling evidence |
| Layering | Controller/API -> application service -> repository -> EF Core/PostgreSQL function/Npgsql -> PostgreSQL |
| Product API | Stateless Container Apps replicas; object authorization and domain rules at API boundary |
| Worker | Embedding, re-embedding, expiry, notifications, scans, privacy/retention, outcomes, exports, outbox publication |
| Job safety | Retry-safe, bounded attempts, leases/timeouts, sanitized errors, poison/dead-letter handling |
| Transactions | Short explicit transactions; connection acceptance/chat creation/outbox; message authorization/persistence; consent changes; sync publication |
| Connection pool | Npgsql pooling; maximum 25 sessions per API replica at initial production sizing |
| Timeouts | API command timeout 15 s; `lock_timeout=3s`; `idle_in_transaction_session_timeout=30s` |
| Retry | Transient connection failures only; capped exponential backoff with jitter; never retry business conflicts blindly |
| Server authority | Mobile/admin state and provider output never override server consent, authorization, policy, or resource state |

# API

| Standard | Constraint |
|---|---|
| Contract | Version-controlled OpenAPI is executable definition; contract tests required |
| Protocol | Versioned REST (`/v1` or one consistent header/media strategy) |
| Authentication | OAuth 2.0/OIDC bearer validation at edge; claims mapped to application permissions |
| Authorization | Community + authenticated actor + permission + ownership/relationship + consent/block/moderation/resource state |
| Errors | `{ code, message, correlation_id, field_errors? }`; safe non-enumerating messages |
| Idempotency | Every externally retryable mutation requires `Idempotency-Key`; same key/different body returns conflict |
| Concurrency | Mutable resources return ETag from `bigint row_version`; require `If-Match` where lost updates matter |
| Pagination | Opaque cursor for feeds, matches, chat, notifications, and sync; no unbounded list endpoints |
| Identifiers | Opaque API IDs; internal sequential keys never exposed |
| Realtime messages | Signal clients to refetch authoritative state; durable write precedes fan-out |
| Forbidden output | Raw vectors, identity subjects, exact/coarse cells, blob paths/SAS URLs, moderation details, and unauthorized profile fields |

| Domain | Minimum endpoints |
|---|---|
| Identity | `POST /auth/register`; `POST /auth/verify`; `POST /auth/session/refresh`; `POST /auth/recover`; `POST /auth/logout` |
| Profile | `GET/PATCH /me/profile`; `GET /members/{id}` |
| Intent/matching | `POST/GET /intents`; `POST /match-requests`; `GET /match-requests/{id}`; `GET /events/{id}/live-match-count?intent_id={id}&threshold={0..1}`; `POST /matches/{id}/feedback` |
| Events | `GET /events`; `POST /events/{id}/register`; `POST/DELETE /events/{id}/live-mode`; `POST /events/{id}/presence` |
| Connections | `POST /connection-requests`; `PATCH /connection-requests/{id}`; `DELETE /connections/{id}`; `POST /members/{id}/block`; `POST /reports` |
| Chat | `GET /conversations`; `GET/POST /conversations/{id}/messages`; `POST /messages/{id}/receipts` |
| Files | `POST /files/upload-init`; `POST /files/{id}/finalize`; `GET /files/{id}/download` |
| Notifications | `GET/PATCH /me/notification-preferences`; `GET /notifications`; `POST /notifications/{id}/ack` |
| Sync | `GET /sync/changes?after={cursor}&limit={n}`; bounded snapshot endpoint |
| Privacy/admin | `POST/GET /me/privacy-requests`; `GET/POST /admin/*` |
| NLP | `POST /v1/matches/search`; health endpoint; pairwise scoring only for test/evaluation |

# Identity and Authorization

| Key | Constraint |
|---|---|
| CIAM | Managed provider owns registration, passwordless verification, recovery, MFA, and token issuance |
| MVP sign-in | Entra External ID email OTP baseline; phone-only requires separate managed-CIAM security/vendor approval; no custom OTP identity system |
| PostgreSQL exclusions | No passwords, OTP values, bearer tokens, refresh tokens, or plaintext sign-in identifiers |
| Identity mapping | Keyed subject hash for equality/uniqueness + encrypted normalized subject + masked display hint |
| Session record | Opaque application binding, provider-session hash, authentication strength, expiry, last-seen, revocation only |
| Device state | Installation ID, member, platform, app version, active/revoked state, last-seen |
| Roles | `MEMBER`, `ADMIN`, `MODERATOR`, `NLP_EVALUATOR` |
| Permission model | Role supplies candidate permissions; object-level policy always re-evaluated |
| Admin | MFA/step-up + permission + object policy; denied sensitive access produces telemetry/audit |
| Service identities | Separate managed identities and schema-level least privilege; migration/owner/runtime/worker/evaluator/audit-reader roles separated |
| Public schema | Revoke default `CREATE` and broad `PUBLIC` privileges; explicit `search_path`; schema-qualified sensitive objects |
| Revocation | Logout, device loss, deletion, and compromise revoke application session and cached authorization immediately |
| Requirements | `FR-01` registration/verification/sign-in/recovery; `FR-02` member profile; `FR-03` admin lifecycle; `FR-04` field-level visibility |

# Database

| Key | Constraint |
|---|---|
| Service | Azure Database for PostgreSQL Flexible Server |
| Engine | PostgreSQL 17; automatic minor updates; rehearse major upgrades |
| Extensions | `vector`, `pg_stat_statements`; allowlist and pin tested versions |
| Logical model | 66 tables + 4 controlled views; schema-separated modular monolith |
| Naming | Physical names use `lower_snake_case`; no quoted mixed-case identifiers |
| Time | `timestamptz` + `CURRENT_TIMESTAMP`; UTC API normalization |
| Concurrency | `bigint row_version`, default 1, shared `BEFORE UPDATE` increment trigger; exposed as ETag |
| IDs | Stable `varchar(64)` API IDs; `GENERATED AS IDENTITY` for internal `bigint` keys |
| JSON | `jsonb` only for bounded validated metadata/snapshots; predicates and relationships remain typed columns |
| Encryption | Sensitive ciphertext in `bytea`; envelope keys remain in Key Vault; ciphertext not used as search index |
| FK deletion | `NO ACTION` default; controlled deletion/anonymization workflows |
| Query boundary | Every service query includes authorization + community/context scope |
| Client access | Prohibited for mobile and admin clients |
| Vector | `vector(1536)` float32 + model version + dimensions + normalized hash |
| Vector query | Eligibility filters first; exact cosine distance (`<=>`) over bounded 50-200 candidates |
| ANN | No HNSW/IVFFlat until representative tests show exact retrieval misses p95; record recall@20, precision@5, memory, build time, write amplification |
| Partitioning | Only after measured need for `Message`, `AuditEvent`, `ProductEvent`, or `EventPresence`; start with purge-friendly date indexes |
| Diagnostics | `pg_stat_statements`; `EXPLAIN (ANALYZE, BUFFERS)` outside production; monitor locks, deadlocks, cache hit, temp files, WAL, vacuum, dead tuples, storage latency |

| Table | Key fields / invariant | Retention / classification |
|---|---|---|
| `core.Community` | `community_id` PK; explicit Release 1 community boundary; `name` unique | Community life + audit; internal |
| `core.Organization` | `organization_id` PK; normalized name/domain indexes; duplicate merge by moderation | Referenced life + archive; business public/confidential |
| `core.OrganizationMember` | Organization/member/start unique; profile visibility controls exposure | Profile life + approved history; personal/business |
| `core.MemberProfile` | `member_id` PK/FK; status + visibility + completeness; no contact fields | Account life + deletion policy; personal/business |
| `core.Sector` | `sector_code` PK; self hierarchy; retire instead of delete | Indefinite/versioned; public reference |
| `core.MemberSector` | Member/sector composite PK; filtered unique primary sector/member | Profile life; business |
| `core.MemberGeography` | Operating market only; never reused for proximity | Profile life; business |
| `core.ProfileFieldVisibility` | Member/field composite PK; audience `PRIVATE\|MATCHED\|CONNECTED\|MEMBERS`; server-enforced | Profile life + consent audit; confidential |
| `core.MemberVerification` | Member/status index; evidence references private `FileAsset`; never member-visible | Account life + audit; restricted |
| `iam.Member` | Opaque `member_id` PK; `PENDING\|ACTIVE\|SUSPENDED\|ANONYMIZED\|DELETED` | Account life + legal/audit; personal |
| `iam.MemberIdentity` | Provider + keyed subject hash unique; subject encrypted; no plaintext/password/token/OTP | Account life; restricted personal |
| `iam.Role` | `role_code` PK; privileged flag; migration-seeded; changes audited | Indefinite while referenced; internal |
| `iam.Permission` | `permission_code` PK; resource/action unique; API policy still applies object checks | Indefinite while referenced; internal |
| `iam.RolePermission` | Role/permission composite PK; grant/revoke audited | Assignment life + audit; internal |
| `iam.MemberRole` | Member/role composite PK; optional expiry/revocation; privileged changes audited | Assignment life + audit; confidential |
| `iam.MemberDevice` | `device_id` PK; active/revoked state; tokens remain in OS keystore | Revocation + 30 days; confidential |
| `iam.AuthSession` | Opaque session PK; provider-session hash unique; auth strength/expiry/revocation only | Session life + 30 days; restricted |
| `consent.ConsentPolicy` | Purpose/version/locale unique; published text immutable | Indefinite/versioned; internal/public text |
| `consent.MemberConsent` | Append-only grant/deny/withdraw evidence; current state from latest valid row | Legal/privacy policy; restricted personal |
| `consent.PrivacyRequest` | Access/correct/delete/export/support case; transitions and export access audited | Regulatory policy; highly restricted |
| `consent.PrivacyRequestTask` | Request/domain/action unique; all required tasks terminal before completion | Request life + legal evidence; highly restricted |
| `event.Venue` | Venue-level coarse cell; exact geofence config restricted | Event life + archive; business/location |
| `event.Event` | `event_id` PK/context; `ends_at > starts_at`; live-mode flag | Event life + archive; business |
| `event.EventMatchingPolicy` | Event/version unique; one active; thresholds/caps 0..1/nonnegative; audited immutable activation | Event life + audit; internal config |
| `event.EventRegistration` | Event/member unique; check-in is not Live Mode consent | Event + approved history; personal |
| `event.LiveModeSession` | One active member/event; consent FK; hard event-bounded expiry; immediate disable | Recommended 24-72 h after event; sensitive location context |
| `event.EventPresence` | Coarse cell + observed/expiry; never expose cell/time | Hours per approved policy; highly sensitive location |
| `social.ConnectionRequest` | No self; pending pair unique; mandatory expiry; block/connection prevents creation | 2 years or approved policy; confidential |
| `social.Connection` | Canonical low/high member pair unique; accepted request unique | Disconnect + policy period; confidential |
| `social.MemberBlock` | Active directional pair unique; suppresses discovery/communication both ways; actor hidden | Until removal + safety policy; highly restricted |
| `social.MemberReport` | Reporter/subject/resource/category; creates/links moderation case | Safety/legal policy; highly restricted |
| `chat.Conversation` | One conversation per accepted connection; created with acceptance | Connection life + message policy; confidential |
| `chat.ConversationParticipant` | Conversation/member composite PK; exactly two matching connection members | Conversation life; confidential |
| `chat.Message` | Client `message_id`; conversation sequence unique; authorize active connection/block every send | Approved chat policy; highly confidential |
| `chat.MessageReceipt` | Message/member composite PK; delivered/read timestamps monotonic | Message life; confidential |
| `storage.FileAsset` | Private blob path/hash unique; verified MIME/size/SHA-256; clean scan + available lifecycle | Purpose-specific; inherits content classification |
| `storage.FileAssetLink` | Asset/resource/type/relationship unique; owning domain validates target; no path exposure | Asset life; restricted metadata |
| `notification.NotificationPolicy` | Scope/purpose/channel/version unique; one active; immutable after activation | Indefinite/versioned; internal config |
| `notification.NotificationPreference` | Member/purpose composite PK; timezone required for quiet hours | Account life; personal |
| `notification.PushToken` | Encrypted token/fingerprint unique; service-only; provider rejection invalidates | Rotation/revocation + grace; secret-like personal |
| `notification.Notification` | Member/channel/dedupe/bucket unique; policy/event snapshots; minimum preview | 90 days default pending approval; confidential |
| `notification.NotificationDeliveryAttempt` | Notification/attempt unique; append-only; terminal/max-attempt stop | Notification life + 30 days; confidential operational |
| `nlp.NlpIntent` | Original + normalized/hash/language/PII/version committed synchronously; embedding async | Active intent + approved history; personal/business text |
| `nlp.NlpEmbedding` | Intent/model composite PK; `vector(1536)`; hash/model unique; one embedding space | Intent life + evaluation; derived sensitive |
| `nlp.NlpModelVersion` | One active model; provider/deployment/dimensions/preprocessor; evaluation-gated promotion | Indefinite/versioned; internal config |
| `nlp.NlpRankingConfig` | Version PK; immutable active interval; weights/threshold validation | Indefinite/versioned; internal config |
| `nlp.NlpProcessingJob` | One active intent/job type; bounded attempts/lease; sanitized error | 30-90 days after completion; confidential |
| `nlp.MatchRequest` | `request_id` PK + request hash; 3-7 limit; immutable version/threshold snapshots | 90 days default pending approval; confidential |
| `nlp.NlpMatchResult` | Request/candidate and request/rank unique; component/final scores + deterministic reasons + versions | 90 days/history policy; confidential/derived |
| `nlp.NlpFeedback` | Append-only correction chain; one current row/requester/result; cycle/same-subject validation | Evaluation policy; de-identify long-term |
| `nlp.MatchSuppression` | At least one member/intent/context target; independent of model score; audited | Expiry + audit; restricted |
| `nlp.EvaluationDataset` | Name/version unique; de-identification/provenance; approved lifecycle | Indefinite/versioned; de-identified research |
| `nlp.EvaluationPair` | Dataset split + gold label; member/org groups cannot cross splits | Dataset life; de-identified research |
| `nlp.EvaluationRun` | Dataset/model/ranking versions + metrics/report; activation evidence | Indefinite/versioned; internal metrics |
| `moderation.ModerationCase` | Source/subject/resource/priority/status; separate access boundary | Safety/legal policy; highly restricted |
| `moderation.ModerationAction` | Append-only case action; reversal is new action | Case retention; highly restricted |
| `moderation.ContentRule` | Rule type/version unique; validated config; review/audit activation | Indefinite/versioned; internal config |
| `moderation.ContentScan` | Resource/scanner/version/result/reasons; no raw malicious copy | Resource life + security period; restricted |
| `ops.OutboxEvent` | Event ID PK; minimal payload; same transaction as aggregate change | 30-90 days after publish; internal/confidential |
| `ops.IdempotencyRecord` | Scope/key composite PK; request hash; cached status/reference; body mismatch conflicts | 24 h-30 days by operation; confidential metadata |
| `ops.BackgroundJob` | Non-NLP durable state; transport remains Service Bus; bounded retry/dead state | 30-90 days after completion; internal/confidential |
| `ops.SyncChange` | Monotonic sequence; member/community scope; safe UPSERT/DELETE/tombstone; authorization recheck | 30-90 days or supported cursor horizon; confidential metadata |
| `ops.RetentionPolicy` | Resource/version unique; one active; immutable after activation; legal-hold behavior | Indefinite/versioned; internal config |
| `ops.RetentionExecution` | Applied policy/window/status/counts/holds/evidence; summary audit required | Policy evidence period; restricted operational |
| `ops.AuditEvent` | Monotonic append-only evidence; allowlisted content-free metadata | Security/legal policy; highly restricted |
| `analytics.ProductEvent` | Pseudonymous member; allowlisted non-content properties; no sensitive content/location | 13 months default pending approval; pseudonymous |

| Controlled view | Source / consumer |
|---|---|
| `nlp.vw_MemberContextEligibility` | `iam` + `core` + `consent` + `event` -> NLP CandidateRepository |
| `nlp.vw_MemberRelationship` | `social.Connection` + `social.MemberBlock` -> NLP CandidateRepository |
| `chat.vw_AuthorizedConversation` | Active connection + participants -> chat authorization |
| `admin.vw_MemberReview` | Redacted member/profile/verification/moderation -> admin API |

| Procedure/function | Atomic responsibility |
|---|---|
| `nlp.GetRequesterIntent` | Active requester intent lookup scoped by member/intent/context |
| `nlp.GetEligibleCandidates` | Eligibility-aware bounded retrieval; maximum 200 |
| `nlp.SaveMatchResults` | Idempotent atomic result persistence |
| `social.AcceptConnectionRequest` | Request transition + canonical connection + conversation/participants + outbox |
| `chat.SaveMessage` | Participant/connection/block authorization + idempotent durable message |
| `event.PurgeExpiredPresence` | Retention-enforced presence purge |
| `notification.TryEnqueue` | Policy + preference + threshold + quiet hours + dedupe + caps + notification/outbox |

# NLP and Matching

| Key | Constraint |
|---|---|
| Service boundary | Separate backend API; mobile never calls model provider; caller retains consent/visibility/action authority |
| Provider | Azure OpenAI / Microsoft Foundry embedding deployment behind adapter |
| Model | `text-embedding-3-small`; 1536 dimensions |
| V1 exclusions | No trained ranker, fine-tuning, chatbot, autonomous agent, or LLM-generated match explanation |
| Input | Separate `WANT` and `OFFER`; original text retained; normalized text is PII-minimized |
| Normalization | Unicode/whitespace/punctuation normalization; language detection; safe abbreviation map; obvious email/phone masking; preserve negation, quantities, locations |
| Cache key | Normalized-text SHA-256 + preprocessing/model version |
| Candidate source | `CandidateRepository` only; read-only eligibility views/functions; 50-200 candidates maximum |
| Eligibility | Active member/profile + visible + shared context + active/unexpired intent + valid embedding + required live presence/consent + no self/block/connection/suppression |
| Scoring direction | A-needs-to-B-offers and B-needs-to-A-offers |
| Reciprocity | Harmonic mean for mutual benefit; documented weighted direction for buyer/supplier flow |
| Result count | Final 3-7 matches; default/maximum 7 |
| Live count | Personalized read-only count uses the same eligibility/ranking path; required intent + 0..1 threshold; optional reciprocal filter; returns evaluated count, 200-candidate cap signal, and version trace; no persisted match request/result |
| Explanation | Approved reason codes/templates derived only from contributing signals; suppress match if no valid explanation |
| Version trace | Preprocessing, model/deployment, dimensions, normalized hash, ranking configuration, threshold on every result/request |
| Provider failure | Reuse last approved embedding/model version; bounded retry/degraded response; rollback active version after failed evaluation |
| Privacy | Provider receives normalized intent and necessary context only; never chat, exact location, or unnecessary PII |
| Telemetry | Request ID, latency, versions, outcome; no raw text by default |
| Requirements | `FR-05` original + normalized intent; `FR-06` vector candidates + configurable rules; `FR-07` explanation + feedback; `FR-08` ineligible suppression; `FR-09` admin thresholds/scope/suppression |

| Ranking component | Current database default |
|---|---:|
| Reciprocal semantic relevance | 0.40 |
| Category compatibility | 0.25 |
| Industry compatibility | 0.15 |
| Business geography fit | 0.10 |
| Intent freshness | 0.10 |
| Event context | 0.00 until evaluation |
| Display threshold | 0.35 |

| Evaluation | Constraint |
|---|---|
| Gold set | At least 200 anonymized pairs: strong, plausible, weak, none, unsafe/ineligible |
| Split | By member/organization; related records remain in one train/validation/test split |
| Metrics | Precision@5; Recall@20; reciprocal precision; request rate; acceptance; not-useful rate; explanation coverage |
| Error taxonomy | Vocabulary ambiguity; missing profile context; geography mismatch; stale intent; policy error; model limitation |
| CI | Fake-provider tests; no Azure dependency |
| Activation | Approved evaluation run + model card + rollback path |

# Events and Location

| Key | Constraint |
|---|---|
| Join | Signed QR/code authoritative; geofence may suggest but does not establish durable membership |
| Context | Event ID is matching `context_id` |
| Live Mode | Explicit purpose-specific consent; active event; configured registration/check-in; hard expiry bounded by event; immediate revocation |
| Presence | Coarse geohash/H3 cell; foreground/check-in/venue source; short TTL; never exact coordinates/history |
| Default proximity | `VENUE`; options `NONE`, `VENUE`, `COARSE_CELL` |
| Presence freshness | Default maximum 15 minutes when proximity used |
| Live filter count | Aggregate only; no candidate identity or presence data; `private, no-store`; a reached 200-candidate cap makes the result bounded rather than an event-wide total |
| Alert threshold | Default final score >= 0.70 |
| Alert caps | Default 2/member/hour; 10/member/event; minimum interval 30 minutes |
| Alert eligibility | Active event policy + consent + registration/check-in/live mode/proximity as configured + member preference + stricter event/channel cap |
| Retention | Live sessions recommended 24-72 hours after event; presence measured in hours per approved policy |
| Forbidden disclosure | Another member's exact/coarse cell, raw observation time, arrival time, or location history |
| Requirements | `FR-10` explicit revocable Live Mode; `FR-11` coarse/check-in presence; `FR-12` high-confidence consent-safe alert; `FR-13` rate limits + quiet hours |

# Connections and Chat

| Key | Constraint |
|---|---|
| Request actions | Send, accept, decline, withdraw, expire, block, report |
| Request creation | No self, active block, existing connection, invalid/expired match, or policy-ineligible pair |
| Request expiry | Mandatory `expires_at`; idempotent worker transitions overdue `PENDING` to `EXPIRED` |
| Acceptance | One transaction creates canonical connection, conversation, two participants, intent snapshots where required, and outbox event |
| Chat gate | Available only after mutual acceptance; reauthorize active connection, participants, blocks, and restrictions on every object/send |
| Persistence | Durable message commit before realtime fan-out |
| Message idempotency | Client-generated `message_id`; unique per conversation |
| Ordering | Monotonic `server_sequence`; client timestamp is UX-only |
| Moderation | Sanitized display; content state retained; authorized policy-bound review only |
| Receipts | Delivered/read timestamps are monotonic |
| Disconnect/block | Immediately closes or suppresses discovery/communication capabilities |
| Requirements | `FR-14` request lifecycle/block/report; `FR-15` accepted-only chat; `FR-16` controlled messages/files; `FR-17` restricted moderation review |

# Files and Object Storage

| Key | Constraint |
|---|---|
| Store | Private Azure Blob Storage; containers allowlisted by purpose; no public blobs |
| Metadata | `storage.FileAsset`; links in `storage.FileAssetLink` |
| Purposes | `CHAT_FILE`, `VERIFICATION_EVIDENCE`, `PRIVACY_EXPORT`, `EVALUATION_REPORT` |
| Upload initiation | Validate authenticated owner, purpose, size, MIME claim; create `UPLOADING`; issue short-lived write URL |
| Finalize | Validate actual type/size/SHA-256; queue scan; persist verified metadata |
| Availability | Only `CLEAN` scan transitions asset to `AVAILABLE` |
| Authorization | Link must target authorized `MESSAGE`, `MEMBER_VERIFICATION`, `PRIVACY_REQUEST`, or `EVALUATION_RUN` resource |
| Download | Re-evaluate caller permission, linked-resource access, purpose, scan, lifecycle; issue short-lived read URL |
| Path safety | Store private `blob_path` and deterministic hash; never store SAS URL or expose raw path |
| Deletion | Lifecycle/retention worker deletes blob and records terminal metadata/evidence/audit |
| Telemetry | No blob URLs, file payloads, credentials, or malicious content |

# Notifications and Realtime

| Key | Constraint |
|---|---|
| Push | APNs + FCM abstraction through Azure Notification Hubs |
| Realtime | Azure SignalR or equivalent managed WebSocket service; earlier Web PubSub option requires one final service selection |
| Development | Polling permitted; free realtime tier is not event capacity |
| Event scale | Paid managed realtime capacity with load-tested buffer |
| Policy | Immutable version per community/purpose/channel; one active version |
| Purposes | `MATCH`, `REQUEST`, `CHAT`, `EVENT`, `SAFETY`, `ACCOUNT` |
| Channels | `PUSH`, `EMAIL`, `IN_APP` |
| Default controls | Dedupe 300 s; maximum 6/hour and 30/day; 3 attempts; retry `60,300,1800` s; TTL 1440 min |
| Quiet hours | `DEFER`, `SUPPRESS`, or restricted `BYPASS`; timezone mandatory when configured |
| Mandatory delivery | Non-opt-out/bypass limited to approved `SAFETY`/`ACCOUNT` purposes |
| Push token | Encrypted/tokenized; notification-service-only access; invalidate on provider rejection |
| Attempt history | Append-only; stop after delivered, expired, or max attempts |
| Payload | Minimum preview data; authenticated client fetches full content |

# Offline Synchronization

| Contract | Constraint |
|---|---|
| First sync | Bounded snapshot returns starting cursor |
| Delta | Ordered authorization-filtered `UPSERT`/`DELETE` records with `next_cursor` and `has_more` |
| Mutation | `Idempotency-Key` + resource ID + `If-Match` where needed; business row/outbox/sync change commit atomically |
| Conflict | `409 RESOURCE_VERSION_CONFLICT` + current version; merge only approved profile fields |
| Tombstone | Immediate local purge; protected records may contain type/ID/sequence only |
| Stale cursor | `410 SYNC_CURSOR_EXPIRED`; discard affected projections; bounded full resync |
| Replay | Same actor/key/body returns original result; same key/different body conflicts |
| Authorization | Server re-evaluates access for every returned sync item |
| Scope | Sync ledger is a mobile projection feed, not a public event bus |

# Security Privacy and Compliance

| Area | Required control |
|---|---|
| Transport/storage | TLS + certificate verification; platform encryption/TDE; Key Vault; managed identity |
| Network | Private networking/private DNS for PostgreSQL, Blob, Key Vault where supported; restricted bootstrap exception only |
| Authorization | Fail closed; server-side checks on every profile, match, request, conversation, file, export, and admin action |
| Consent | Purpose-specific, versioned, revocable evidence for terms, location, Live Mode, notifications, analytics |
| Optional collection | Off by default; withdrawal effective immediately |
| Location | No coordinate history; no member exact/coarse disclosure; bounded short-retention presence only |
| AI boundary | Normalized intent only; no raw chat, exact location, or unnecessary PII to model provider |
| Abuse | Subject/device/IP rate limits; block/report; spam/flood controls; audited restricted review |
| Files | Allowlisted type/size; clean malware/content scan; private storage; expiring URLs |
| Data rights | Access/export, correction, withdrawal, deletion/anonymization with per-domain verifiable completion |
| Privacy completion | Required `PrivacyRequestTask` rows terminal (`COMPLETED` or approved `EXEMPTED`) before request completion |
| Retention | Immutable active `RetentionPolicy` versions; retryable `RetentionExecution` with counts, holds, evidence hash, audit |
| Audit | Append-only, content-free metadata; no message, intent, token, or location content |
| QA data | Synthetic or formally masked only; no uncontrolled production copies |
| Legal | Malaysia PDPA, residency, retention, cross-border processing, and data-region choices require qualified counsel approval |
| Threat tests | Enumeration; unauthorized chat/file/export; location inference; prompt injection/data leakage; spam/harassment; malicious files; request flooding |

# Moderation

| Key | Constraint |
|---|---|
| Cases | Unified source from member report, automated scan, or admin review |
| Access | Separate safety permissions; redact reporter identity where feasible |
| Actions | Append-only; reversal recorded as new action |
| Rules | Versioned, validated configuration; activation/change reviewed and audited |
| Scans | File/profile/intent/message scan; sanitized reason codes; no raw malicious content in database |
| Match impact | Suppression is independent of model score and checked before display/request |

# Analytics

| Key | Constraint |
|---|---|
| Events | Profile completion, match view, request, acceptance, chat, feedback, and operational funnel events |
| Identity | Environment-specific salted member pseudonym; no direct member ID in product events |
| Properties | Allowlisted non-content dimensions only |
| Exclusions | Raw intent, contact data, message text, file content, exact/coarse location |
| Default retention | 13 months; final policy approval required |
| Isolation | Export/partition analytics from transactional paths as volume requires |
| Requirements | `FR-19` privacy-safe activation/matching/request/acceptance/chat/feedback analytics; `FR-20` de-identified labelled-pair/model/threshold/feedback reporting |

# Observability Reliability and Recovery

| Key | Constraint |
|---|---|
| Instrumentation | OpenTelemetry + Application Insights + structured logs + metrics + traces |
| Dashboards/alerts | API latency; worker latency/backlog/queue age; delivery failure; consent error; database saturation; match quality; security events; AI usage |
| Log exclusions | Tokens, raw messages, raw intent, identity subjects, member location, blob/SAS URLs, provider secrets/errors containing content |
| Availability | MVP pilot target 99.5% monthly |
| Degradation | Matching/chat/realtime may degrade independently; consent and authorization never fail open |
| RPO/RTO | Initial test/pilot: RPO <= 15 min; RTO <= 4 h |
| Backup | Production PITR 14 days; approve 35-day/geo-redundant if required; quarterly timed restore drill |
| HA | Zone-redundant HA before general availability |
| Rollback | Container revision rollback; ranking/model active-version switch; configuration rollback; application rollback for migrations |
| Event operations | Warm capacity, load-tested runbook, deployment freeze, rollback, and post-event scale-down |

# Deployment and Infrastructure

| Layer | Azure baseline |
|---|---|
| Ingress | API Management or equivalent edge; OIDC validation, throttling, correlation ID; WAF where available |
| Compute | Azure Container Apps Consumption for API; Container Apps Job/worker for asynchronous work |
| Database | Azure Database for PostgreSQL Flexible Server |
| Messaging | Azure Service Bus queues/topics for embedding, scan, notification, privacy/retention work; duplicate detection, bounded retry, dead-letter monitoring |
| Realtime | Azure SignalR or equivalent managed WebSocket service; final product choice pending |
| Push | Azure Notification Hubs + APNs/FCM |
| Files | Private Azure Blob Storage + lifecycle policies |
| Secrets | Azure Key Vault + managed identities |
| NLP | Azure OpenAI / Microsoft Foundry embeddings |
| Moderation | Azure AI Content Safety F0 for development; production sizing from measured load |
| Admin | Azure Static Web Apps |
| IaC | Terraform + Azure Developer CLI; environment parameter files |
| CI/CD | GitHub Actions + OpenID Connect federation; no saved Azure client secret |
| Rollout | Container Apps revisions + health checks + explicit rollback |
| Tags | `product`, `environment`, `owner`, `costCenter`, `dataClass`, `expiryDate` |
| Dev scaling | `minReplicas=0`; free/low-cost services; no SLA; one development environment |
| Event scaling | Warm one or more API replicas; paid realtime/push; raised PostgreSQL capacity; scale down after event |

# PostgreSQL Production Capacity

| Area | Baseline / gate |
|---|---|
| Production compute | General Purpose; 4 vCores initially; test 8-vCore scale point; never Burstable |
| Production storage | 200 GB; automatic-growth monitoring; alerts at 70% and 85%; cannot shrink in place |
| Development/QA | Burstable permitted; 1-2 vCores; 32-64 GB; no HA; 7-day PITR |
| PgBouncer | Managed endpoint port 6432; transaction pooling |
| Session budget | <=200 app DB sessions: <=25/API replica x 8; 20 workers; 5 admin; >=25 reserve |
| Session restrictions | No persistent session state, session advisory locks, or cross-transaction temp-table dependence |
| Dataset gate | 60,000 members + representative intents/relationships/events/messages/audit rows |
| Active-user gate | 3,000 active users through API/PgBouncer |
| Throughput stages | 300, 600, 1,000 requests/s; document saturation curve and safe point |
| Failover gate | Forced HA failover under mixed load; RPO 0 for HA test; no duplicate writes from retries |

# Performance

| Metric | Target |
|---|---:|
| Cached match feed | p95 <= 1.5 s |
| Request/message acknowledgement | p95 <= 500 ms at agreed/event load |
| Live view | <2 s on mid-range Android over 3G; cached render immediate |
| Candidate set | 50-200 after authorization/eligibility filters |
| Match cards | 3-7 |
| Connection safety | No pool exhaustion/connection storm at approved load |

# Testing and Quality Gates

| Gate | Required evidence |
|---|---|
| Schema | Clean PostgreSQL build + repeatable upgrade from prior baseline; FK/check/unique/concurrency/retention tests |
| Seed/config | Deterministic versioned roles, sectors, consent policies, ranking config, event/notification policies, test events |
| API | OpenAPI client stubs build; positive/negative contract tests; stable errors/idempotency/concurrency |
| Authorization | Allow/deny matrix; blocked/unconnected cannot match/chat/download; members cannot access privileged endpoints |
| Identity | Register, verify, recover, refresh, revoke, MFA/step-up; no token/OTP/password leakage |
| NLP | Unit normalization/PII/reciprocity/reasons; golden pairs; fake provider; privacy; load; version trace |
| Events | Grant/deny/revoke/expire/purge; registration/check-in/live/proximity combinations; alert thresholds/caps |
| Chat/files | Canonical single connection/conversation; retry no duplicate; clean-scan authorization; block/disconnect denial |
| Notifications | Opt-out, mandatory restrictions, quiet hours, dedupe, caps, TTL, retry, invalid token, acknowledgements under concurrency |
| Offline | Snapshot, delta, tombstone, duplicate command, conflict, expired cursor on iOS and Android |
| Privacy/retention | Every task/execution terminal with evidence; failures/exemptions visible and audited |
| Security | No unresolved critical/high findings; least privilege; secret scanning; TLS/denied paths |
| Recovery | Timed PITR restore; dependency failure; graceful degradation; failover |
| Accessibility | Automated + manual WCAG 2.2 AA-intent evidence |
| Operations | Outbox/retry/dead-letter evidence; dashboards/alert tests; backup status; migration log; runbook |
| Release | Severity-1/blocker/critical defects closed; rollback rehearsed; QA evidence pack approved |

# Toolchain

| Category | Stack |
|---|---|
| Editor | VS Code + C# + Python + ESLint + Terraform extensions |
| Mobile | Node.js LTS; pnpm; Expo CLI; Android Studio; Xcode |
| Backend | .NET 10 SDK; Docker Desktop; PostgreSQL/SQL tooling |
| NLP lab | Python 3.12; uv; JupyterLab; pandas; numpy; scikit-learn; sentence-transformers |
| API | OpenAPI; Bruno or Postman |
| Unit/integration | xUnit; Testcontainers |
| Web/mobile E2E | Playwright; Maestro |
| Load | k6 |
| Delivery | Git; GitHub Issues/Projects; GitHub Actions; Terraform; azd |
| Governance | ADRs; protected branches; secret scanning; release tags |

# Repository

| Path | Contents |
|---|---|
| `apps/mobile` | Expo app; SQLite projections; outbox; device integrations |
| `apps/admin` | React/Vite staff console; live-ops dashboard |
| `services/api` | Modular monolith domains and authorization |
| `services/worker` | Embedding; match generation; expiry; scans; notification; outcomes; privacy/retention |
| `services/nlp` | Standalone NLP API; matching pipeline; provider adapters; CandidateRepository |
| `packages/contracts` | OpenAPI TypeScript client; schemas; reason-code catalog |
| `ai/notebooks` | Anonymized datasets; baseline; evaluation; error analysis; model card |
| `infra` | Terraform modules; environment parameters; budgets; dashboards; alerts |
| `docs/adr` | Identity; offline; retention; ranking; deployment; service-selection decisions |

# Cost Controls

| Stage | Envelope / control |
|---|---|
| Learning | USD 0-25/month; free SQL offer, scale-to-zero API, Static Web Apps Free, Content Safety F0, email OTP |
| Small pilot | USD 25-100/month; PostgreSQL compute/storage, low API traffic, <500 push devices |
| Event launch | USD 100-400/month + SMS if used; warm API, paid realtime, Notification Hubs Basic, raised PostgreSQL |
| Budget alerts | 50%, 80%, 100% subscription thresholds |
| Telemetry | Sampling + conservative daily cap |
| AI | Token budgets; short normalized intents; embeddings only |
| Exclusions | App-store accounts, domain, taxes, SMS, staff time; verify regional price before purchase |

# Product Metrics

| Metric | Release 1 target |
|---|---:|
| Profile completion | >=70% of activated members |
| Match-to-request | >=10% of viewed matches |
| Request acceptance | >=35% |
| Time to first relevant match | <10 min after profile completion |
| NLP labelled-set precision | >=80% positive |

# Deferred Technology

| Technology/capability | Adoption gate |
|---|---|
| AKS/Kubernetes | Independently owned services or hard platform requirement |
| Microservices | Clear team or independent scaling boundary |
| Azure AI Search/dedicated vector DB | Millions of records or measured ANN/hybrid-search need |
| Azure Managed Redis | Measured hot-read/coordination bottleneck |
| Service Bus Premium | Ordering, topics, transactions, or duplicate detection beyond selected tier |
| Generative agent/LLM explanation | Validated user job + safety/evaluation controls |
| Fine-tuning | Thousands of reviewed labels with stable demonstrated failure |
| Precise location history | Prohibited |
| Public directory/payments/marketplace/CRM/ERP | Post-MVP product, legal, tenant-isolation, and security readiness |

# Pending Approvals

| Decision | Required approval |
|---|---|
| Community model | One OLGA community baseline vs future tenant isolation |
| Identity | Final provider; email, mobile, or both; phone-only exception path |
| Realtime | Azure SignalR vs Web PubSub equivalent |
| Async transport | Service Bus baseline vs lower-cost Queue Storage where enterprise semantics are unnecessary |
| Retention | Identity, chat, file purpose, NLP input/result/feedback, audit, privacy case periods |
| Events | Registration/check-in prerequisites; foreground coarse location requirement |
| Event alerts | 0.70 threshold; 2/hour; 10/event; 30-minute interval |
| Notifications | Purpose/channel defaults, hourly/daily caps, TTL, mandatory safety/account bypass |
| Visibility | Profile-field audience matrix before/after connection |
| Network | Private endpoints/VNet requirements for each production tier |
| Database | Vector extension allowlist/version; exact-search load result; ANN go/no-go |
| Recovery | 14 vs 35-day/geo-redundant backup; production RPO/RTO |
| Legal | Malaysia PDPA, residency, cross-border processing, deletion evidence |
| Outcome ledger | HLD requires meeting outcome/usefulness/follow-up and acceptance-time intent snapshots; database v2.4 defines no ledger/follow-up tables or final API contract |
