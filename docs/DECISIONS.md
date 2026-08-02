# Relay Decisions

## 001 — Runtime baseline

Status: accepted

.NET 10, EF Core 10, Node 24, Next.js 16, React 19, PostgreSQL 18. The .NET SDK is installed project-locally and the full system runs through Docker Compose. Committed lockfiles pin exact versions for both runtimes.

## 002 — PostgreSQL as the durable queue

Status: accepted

The API creates an event and queued delivery in one PostgreSQL transaction. A background worker claims deliveries with `FOR UPDATE SKIP LOCKED`. No external message broker is needed for this scope.

## 003 — Exact local URL allowlist

Status: accepted

Registered URLs must match the configured receiver origin and `/webhooks/{UUID}` path exactly. Redirects, alternate hosts, IP literals, userinfo, queries, and fragments are rejected at both registration and dispatch.

## 004 — Runtime signing-key protection

Status: accepted

The simulator generates signing keys at runtime. The API protects stored keys with ASP.NET Core Data Protection. API and worker share an uncommitted Docker volume key ring. This is a local-demo boundary, not a production secret-management claim.

## 005 — Same-origin dashboard proxy

Status: accepted

The browser communicates through fixed Next.js rewrites to internal API and receiver services. Only the dashboard is published to the host; browser CORS configuration is unnecessary.

## 006 — Reproducible dependency pins

Status: accepted

Live package registries and vulnerability feeds determine exact versions. npm pins specific tarballs for Next.js, Vitest, Vite, and their cross-platform native packages where registry metadata lagged behind published releases. The lockfile retains every supported Next SWC, Lightning CSS, and Rolldown platform so clean Linux builds do not invoke lockfile repair or miss a native test runner. NuGet pins `Microsoft.OpenApi` to 2.11.0 to resolve a transitive advisory. Lockfiles record the complete integrity-checked graph.

npm currently reports high-severity advisories for PostCSS and Sharp required by Next.js with no compatible fix available. Relay does not accept CSS input and does not use Next.js image processing. Unsupported dependency overrides are not used; the constraint is reviewed when upstream releases are available.

## 007 — Preserve delivery envelopes as text

Status: accepted

Delivery envelope JSON is stored in a PostgreSQL `text` column. `jsonb` may rewrite whitespace or property ordering; Relay must preserve the exact bytes used for its hash and HMAC signature.

## 008 — Versioned exact-byte signing

Status: accepted

The worker signs `v1\n<unix-seconds>\n<delivery-id>\n<raw-body>` with HMAC-SHA256 and sends the result as base64 in `X-Relay-Signature`. The receiver accepts timestamps within five minutes and compares signatures in constant time. A delivery identifier with the same body is acknowledged without re-applying; reuse with a different body is rejected with 409.

## 009 — Fixed retry contract

Status: accepted

Maximum four total attempts. Delays after attempts 1–3: 1 s, 2 s, 4 s. Retryable failures: HTTP 408, 429, 500–599, timeout, and transport error. Everything else (other HTTP responses, invalid target, unavailable signing secret) is terminal immediately.

State flow: Queued → Processing → RetryScheduled → Processing → … → Succeeded | Failed. A successful later attempt clears the delivery-level error; individual failed attempt records are preserved.

## 010 — Claim lease and stale-claim recovery

Status: accepted

Each claim sets a 30-second lease (`ClaimExpiresAtUtc`). If a worker crashes or stalls, the recovery scan finds `Processing` deliveries past their lease, marks the in-progress attempt as failed with `claim_expired`, and either schedules a retry (if attempts remain) or marks the delivery terminally failed. This uses `TimeProvider` so tests do not sleep.

The subsequent `BackfillRetryClaims` migration backfills attempt counts from existing attempt rows. Deliveries and attempts that were already processing are marked failed with `migration_backfill`, completed, and stripped of their old claim token so databases that had already applied the lease schema cannot remain stranded after upgrade.

## 011 — Idempotent manual replay

Status: accepted

`POST /api/deliveries/{id}/replays` accepts exactly one `Idempotency-Key` header. Only terminally failed deliveries can be replayed. A replay creates a new delivery with a fresh envelope, hash, and correlation ID while keeping the same event, endpoint, type, and payload. Attempt numbering starts at 1. Repeated requests with the same source and key return the same replay delivery with `Idempotency-Replayed: true`.

The one-delivery-per-event uniqueness constraint is replaced by a non-unique index. A filtered unique index on `(ReplayOfDeliveryId, ReplayIdempotencyKey)` prevents duplicate replays.

## 012 — Least-privilege CI

Status: accepted

CI uses Ubuntu jobs: frontend checks, Release build and unit tests, Docker-backed integration tests, and Compose-backed Playwright workflows. Official GitHub Actions are pinned to reviewed commit SHAs and the workflow has only `contents: read` permission. CI does not publish packages, images, or deployments.
