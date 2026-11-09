# Test plan (Milestone A)

| Layer | Tooling | Location | Runs where |
|---|---|---|---|
| Unit (domain, calendar, rendering) | xUnit 2.9, PdfPig | `tests/Serk.Clinic.UnitTests` | `dotnet test` |
| Architecture (module boundaries, license guard) | NetArchTest, nuspec scan | `tests/Serk.Clinic.ArchitectureTests` | `dotnet test` |
| Integration / security / concurrency | WebApplicationFactory + **real PostgreSQL 16** (Testcontainers, or `SERK_TEST_PG_ADMIN`) | `tests/Serk.Clinic.IntegrationTests` | `dotnet test` (fails, never skips, without a DB) |
| API contract | OpenAPI path snapshot | `docs/api/openapi-paths-v1.txt` | integration suite (`SERK_UPDATE_API_SNAPSHOT=1` to regenerate) |
| Frontend unit | Vitest | `src/web/src/**/*.test.ts` | `npm test` |
| E2E + accessibility | Playwright 1.63 + axe-core (WCAG 2.0/2.1/2.2 A/AA tags) at 1366×768 | `src/web/e2e` | `tools/e2e.sh` |
| Dependency security | NuGet audit (warnings as errors), `dotnet list package --vulnerable`, `npm audit` | CI + local | |

**Principles:** synthetic data only; no skipped tests; failing infrastructure fails the run; concurrency tests assert both
winners and losers; each release-critical ID maps to tests in `RELEASE_CRITICAL_SCENARIOS.csv`.

**Not yet covered:** pixel-level PDF visual regression, manual screen-reader testing, DAST, load/performance (targets in
the architecture kit are unverified hypotheses), restore drills, process-kill resilience, EF-model vs schema drift.
