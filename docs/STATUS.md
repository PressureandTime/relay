# Relay Status

## Capabilities

| Area | State | Coverage |
|------|-------|----------|
| Endpoint registration | ✓ | URL allowlist, signing key protection, validation |
| Idempotent event creation | ✓ | SHA-256 fingerprint, conflict detection, concurrency |
| HMAC-signed delivery | ✓ | Versioned signing string, exact-byte envelope |
| Automatic retries | ✓ | Exponential backoff, 4 attempts max, retryable classification |
| Stale-claim recovery | ✓ | 30-second lease, expired claims fail and re-queue |
| Manual replay | ✓ | Idempotent, new envelope and correlation, lineage tracking |
| Receiver simulator | ✓ | success, retryThenSucceed, failUntilReplay, alwaysFail |
| Dashboard | ✓ | Live polling, retry/replay UI, accessible, responsive |

## Test matrix

| Suite | Tests | Tooling |
|-------|-------|---------|
| .NET unit | Domain state, signing, retry classification, delays, lease | xUnit |
| .NET integration | Migrations, claiming, retry, exhaustion, permanent failure, stale recovery, replay, concurrency, sanitized responses | Testcontainers + PostgreSQL 18 |
| Frontend unit | Contract normalization, privacy, new states, replay fields | Vitest |
| E2E | Success delivery, retry-then-success, fail-then-replay, reload persistence, keyboard | Playwright + Chromium |

## Dependency notes

- npm pins Next.js 16.2.12 directly from registry tarballs. PostCSS and Sharp advisories remain open pending a compatible Next.js release.
- Vitest 4.1.0 and Vite 7.3.5 are pinned to avoid critical/high advisory ranges.
- NuGet pins `Microsoft.OpenApi` to 2.11.0 to resolve a transitive high-severity advisory.
- Playwright is pinned to 1.58.2; newer metadata referenced an unpublished runtime.

## Current blockers

None.
