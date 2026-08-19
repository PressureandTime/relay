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

Live package registries and vulnerability feeds determine exact versions. npm pins specific tarballs for Next.js, Vitest, Vite, and their cross-platform native packages where registry metadata lagged behind published releases. The lockfile retains every supported Next SWC, Lightning CSS, and Rolldown platform so clean Linux builds do not invoke lockfile repair or miss a native test runner. NuGet pins `Microsoft.OpenApi` to 2.11.0 and SSH.NET to 2026.0.0 to resolve transitive advisories. Lockfiles record the complete integrity-checked graph.

npm currently reports high-severity advisories in development tooling and the Next.js dependency tree. PostCSS and Sharp have no compatible fix in the pinned stack. Relay does not accept CSS input or use Next.js image processing. Unsupported dependency overrides are not used; the constraint is reviewed when compatible upstream releases are available.

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

## 013 — Exact delivery-history filters

Status: accepted

`GET /api/deliveries` accepts optional `state`, `endpointId`, and `eventType` query parameters. Each filter is an exact match, multiple filters are combined with AND, and the existing bounded limit is applied after filtering. State matching is case-insensitive at the HTTP boundary and event-type matching remains case-sensitive, consistent with event creation and storage. Invalid state or event-type values return a validation response instead of an empty result.

The dashboard keeps draft filter values separate from applied filters. Apply performs one request, Reset restores the unfiltered history, and automatic refreshes reuse the applied filters. Filtering remains server-side so the result is correct before the 20-row dashboard limit is applied.

## 014 — Reversible endpoint lifecycle

Status: accepted

Webhook endpoints have an explicit `Active` or `Disabled` state. New endpoints are active. Disable and reactivate operations are idempotent, return the current endpoint representation, and do not delete endpoint or delivery history.

A disabled endpoint rejects new event submissions and new manual replays with `409 Conflict`. An identical event or replay request that already succeeded idempotently still returns its original accepted response, even if the endpoint was disabled later. Deliveries that were queued or retry-scheduled before disablement continue through the existing worker pipeline; disabling an endpoint is an intake control, not a cancellation mechanism.

## 015 — Stable delivery-history cursors

Status: accepted

`GET /api/deliveries` returns `{ items, nextCursor }` and orders deliveries by `CreatedAtUtc DESC, Id DESC`. A continuation cursor records the last returned position and the normalized filter values. It is versioned JSON encoded as unpadded base64url, contains no secret, and is rejected when malformed or reused with different filters. Pages fetch one extra row to determine whether another cursor exists.

This is forward keyset traversal, not a database snapshot. Deliveries created after the first page appear after Refresh rather than in an older continuation page. State changes can move records into or out of a filtered result between requests; Refresh reconciles the list. Apply, Reset, manual Refresh, and terminal polling replace the dashboard with page one. Load more appends in server order, removes duplicate IDs defensively, and retains the existing list and cursor when a continuation request fails.

No database index is added for this local bounded dataset. An index should follow measured query evidence rather than an unsupported performance claim.

## 016 — Event-group delivery retention

Status: accepted

The worker removes completed webhook event groups after a configurable retention period. The default is 30 days, cleanup runs at most once per hour, and one pass removes at most 100 event groups. The age threshold is strict: a delivery completed exactly at the cutoff remains until a later pass.

An event is eligible only when it has at least one delivery and every delivery in the group is `Succeeded` or `Failed` with `CompletedAtUtc` before the cutoff. Originals and replays therefore remain together. Queued, processing, retry-scheduled, recently completed, and incomplete deliveries preserve the whole event group. Deleting the event uses the existing database cascades to remove its deliveries and attempts; the endpoint remains.

Retention also removes the event's idempotency record and payload. Reusing the same endpoint and idempotency key after expiry creates a new event, which is the expected boundary once the original record no longer exists. The worker logs only passes that remove data. No new table, index, migration, hosted service, or external scheduler is introduced.

## 017 — Safe dashboard event retries

Status: accepted

The dashboard keeps one in-memory submission intent for the normalized event request body: endpoint identifier, trimmed event type, and parsed payload. Repeating the same submission after a failed or unknown response reuses its `Idempotency-Key`, allowing the API to return the original event and delivery instead of creating a duplicate. A changed request body receives a new key.

The intent is cleared only after the dashboard validates an accepted response. It is also cleared by that success so deliberately submitting the same form again creates a new event. The intent is not written to local or session storage; event payloads and idempotency keys therefore do not persist across a page reload.
