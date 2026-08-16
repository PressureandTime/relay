# Relay Roadmap

## Completed

- Endpoint registration with URL allowlist and signing-key protection.
- Idempotent event creation with transactional delivery queuing.
- HMAC-signed asynchronous delivery through a background worker.
- Automatic retries with 1 s, 2 s, and 4 s exponential backoff and stale-claim recovery.
- Idempotent manual replay for failed deliveries.
- Configurable receiver simulator (success, retry-then-succeed, fail-until-replay, always-fail).
- Operations dashboard with live polling and replay controls.
- Server-side delivery-history filtering by state, endpoint, and event type.
- Stable cursor-based delivery-history pagination with dashboard continuation.
- Reversible endpoint disable/reactivate controls that stop new intake while queued deliveries continue.
- xUnit unit tests, Testcontainers integration tests, Vitest, and Playwright workflows for success, retry, and replay.

## Next candidates

- Delivery retention policy and scheduled cleanup.
- Dashboard dark mode.
- Additional browser coverage for validation and concurrent idempotency scenarios.

## Not planned

- Arbitrary webhook destinations.
- Authentication, multitenancy, and billing.
- Azure or other cloud infrastructure.
- OpenTelemetry packages.
- AI features or paid APIs.
- Public deployment.
