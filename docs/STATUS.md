# Relay Status

## Capabilities

| Area | State | Coverage |
|------|-------|----------|
| Endpoint registration | ✓ | URL allowlist, signing key protection, validation |
| Idempotent event creation | ✓ | SHA-256 fingerprint, conflict detection, concurrency |
| HMAC-signed delivery | ✓ | Versioned signing string, exact-byte envelope |
| Automatic retries | ✓ | Exponential 1 s, 2 s, and 4 s backoff; 4 attempts max; retryable classification |
| Stale-claim recovery | ✓ | 30-second lease, expired claims re-queue |
| Manual replay | ✓ | Idempotent, new envelope and correlation, lineage tracking |
| Delivery filtering | ✓ | Exact state, endpoint, and event-type filters; combined server-side |
| Receiver simulator | ✓ | success, retryThenSucceed, failUntilReplay, alwaysFail |
| Dashboard | ✓ | Live polling, replay UI, accessible, responsive |

## Test matrix

| Suite | Tests | Tooling |
|-------|-------|---------|
| .NET unit | 25 | xUnit |
| .NET integration | 25 | Testcontainers + PostgreSQL 18 |
| Frontend unit | 9 | Vitest |
| E2E | 3 workflows: success and filtering, retry, replay | Playwright + Chromium |

## Known issues

- `npm audit --package-lock-only --audit-level=high` exits 1. The current lockfile reports three high findings and no low or critical findings. All three are in Next.js and its exact PostCSS/Sharp dependency chain; npm reports no compatible fix. Relay does not accept CSS input or use Next.js image processing, and incompatible overrides are not used. Recheck this constraint when a supported Next.js release is available.

## Dependency notes

- Next.js 16.2.12 resolves PostCSS 8.4.31 and Sharp 0.34.5.
- Vite 8.0.16 resolves PostCSS 8.5.18, Lightning CSS 1.32.0, and Rolldown 1.0.3. Its previous esbuild dependency and advisory are no longer present.
- Vitest 4.1.1 supports Vite 8. Cross-platform Next SWC, Lightning CSS, and Rolldown native tarballs are pinned because registry metadata lags their published releases.
- Incompatible PostCSS and Sharp overrides are not used.
- SSH.NET is transitively pinned to 2026.0.0 because Testcontainers otherwise resolves the vulnerable 2025.1.0 release. The NuGet vulnerability check reports no vulnerable packages for the solution.
- Playwright is pinned to 1.58.2.

## Verified locally

- .NET Release build: 0 warnings, 0 errors.
- xUnit: 25 unit tests and 25 PostgreSQL integration tests passed.
- Frontend: clean `npm ci`, ESLint, TypeScript, 9 Vitest tests, and the Next.js production build passed. A separate Linux container clean install and Vitest run also passed before the filtering slice.
- Docker Compose: npm 11.16 clean install and all images built; migration exited 0; API, worker, receiver, PostgreSQL, and dashboard became healthy.
- Playwright: 3 Chromium workflows passed, including combined state, endpoint, and event-type filtering and reset.
