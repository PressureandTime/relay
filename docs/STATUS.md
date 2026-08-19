# Relay Status

## Capabilities

| Area | State | Coverage |
|------|-------|----------|
| Endpoint registration | ✓ | URL allowlist, signing key protection, validation |
| Endpoint lifecycle | ✓ | Idempotent disable/reactivate; disabled intake; queued work continues |
| Idempotent event creation | ✓ | SHA-256 fingerprint, conflict detection, concurrency |
| HMAC-signed delivery | ✓ | Versioned signing string, exact-byte envelope |
| Automatic retries | ✓ | Exponential 1 s, 2 s, and 4 s backoff; 4 attempts max; retryable classification |
| Stale-claim recovery | ✓ | 30-second lease, expired claims re-queue |
| Manual replay | ✓ | Idempotent, new envelope and correlation, lineage tracking |
| Delivery filtering | ✓ | Exact state, endpoint, and event-type filters; combined server-side |
| Delivery pagination | ✓ | Stable keyset cursor; filter binding; dashboard continuation and retry |
| Delivery retention | ✓ | Configurable 30-day default; bounded event-group cleanup; active-work preservation |
| Receiver simulator | ✓ | success, retryThenSucceed, failUntilReplay, alwaysFail |
| Dashboard | ✓ | Endpoint lifecycle, live polling, filtering, pagination, replay UI, accessible, responsive |

## Test matrix

| Suite | Tests | Tooling |
|-------|-------|---------|
| .NET unit | 27 | xUnit |
| .NET integration | 35 | Testcontainers + PostgreSQL 18 |
| Frontend unit | 13 | Vitest |
| E2E | 3 workflows: lifecycle/success/filtering/pagination, retry, replay | Playwright + Chromium |

## Known issues

- `npm audit --package-lock-only --audit-level=high` exits 1. The current lockfile reports eight high findings and no critical findings across brace-expansion, js-yaml, nanoid, PostCSS, and Sharp. PostCSS and Sharp have no compatible fix in the pinned Next.js stack. npm proposes updates for some development-tool findings, but its lockfile-only remediation dry run currently fails because the registry has no semver release matching the pinned js-yaml 4.3.0 tarball. No unsupported override or breaking ESLint upgrade was applied.

## Dependency notes

- Next.js 16.2.12 resolves PostCSS 8.4.31 and Sharp 0.34.5.
- Vite 8.0.16 resolves PostCSS 8.5.18, Lightning CSS 1.32.0, and Rolldown 1.0.3. Its previous esbuild dependency and advisory are no longer present.
- Vitest 4.1.1 supports Vite 8. Cross-platform Next SWC, Lightning CSS, and Rolldown native tarballs are pinned because registry metadata lags their published releases.
- Incompatible PostCSS and Sharp overrides are not used.
- SSH.NET is transitively pinned to 2026.0.0 because Testcontainers otherwise resolves the vulnerable 2025.1.0 release. The NuGet vulnerability check reports no vulnerable packages for the solution.
- Playwright is pinned to 1.58.2.

## Verified locally

- .NET Release build: 0 warnings, 0 errors.
- xUnit: 27 unit tests and 35 PostgreSQL integration tests passed.
- Frontend: ESLint, TypeScript, 13 Vitest tests, and the Next.js production build passed.
- Docker Compose: npm 11.16 clean install and all images built; migration exited 0; API, worker, receiver, PostgreSQL, and dashboard became healthy.
- Playwright: 3 Chromium workflows passed, including endpoint disable/reactivate, combined delivery filtering, cursor pagination with continuation retry, retry scheduling, and replay.
