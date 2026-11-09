# Test evidence index

| Date (UTC) | Commit | What | Result | Evidence |
|---|---|---|---|---|
| 2026-11-09 14:38 | f5e7c21 | Release build; unit + architecture + integration | build 0 warnings/0 errors; 51 + 15 + 53 passed, 0 failed, 0 skipped | `docs/evidence/milestone-A/build-20261109T143855Z.log`, `test-20261109T143855Z.log` |
| 2026-11-09 14:59 | (review round 1 fixes, see BUILD_STATUS) | Release build; unit + architecture + integration; E2E; NuGet/npm audit | 0 warnings; 53 + 15 + 64 passed, 0 failed, 0 skipped; E2E 4/4 passed; 0 known vulnerable packages | `docs/evidence/milestone-A/*-20261109T145933Z.log`, `screenshots/` |

| 2026-11-09 15:15 | dental commit | tools/verify.sh (all CI gates) + E2E incl. dental | 71 unit + 18 architecture + 69 integration passed, 0 skipped; Vitest 9; E2E 5/5; 0 vulnerabilities | `docs/evidence/milestone-A/verify-20261109T151523Z.log`, `e2e-20261109T151523Z.log`, `screenshots/05-dental-chart.png` |
| 2026-11-09 15:21 | dd88759 | GitHub Actions CI run #2 (backend, web, e2e jobs) | **PASS** all three jobs | https://github.com/Egziu/Serk-Clinic-os/actions/runs/42643113858 |
| 2026-11-09 15:07 | 3fc3f90 | GitHub Actions CI run #1 | **FAILED** backend format check (web job passed; e2e skipped) | https://github.com/Egziu/Serk-Clinic-os/actions/runs/42641239697 |

TRX files are produced under `artifacts/` (git-ignored) and uploaded by CI. Screenshots contain synthetic data only.
CI workflow `.github/workflows/ci.yml` observed passing on GitHub (run #2).

| 2026-11-09 18:06 | branding/automation/analytics commit (see git log) | tools/verify.sh (all CI gates) after review round 2 fixes | 111 unit + 18 architecture + 89 integration passed, 0 failed, 0 skipped; Vitest 17; npm/NuGet 0 vulnerabilities; format + license gates PASS | `docs/evidence/milestone-A/verify-20261109T180627Z.log` |
| 2026-11-09 18:06 | same | Playwright E2E incl. management (settings, colour, uploads, printers, dashboard light/dark, signature), slip printing, vertical slice | 9/9 passed; axe 0 serious/critical on all audited screens | `docs/evidence/milestone-A/e2e-20261109T180627Z.log`, `screenshots/06-08` |
| 2026-11-09 (before 18:06) | same | Mutation check of BRAND-005 (removed in-effect filter) | test FAILED on mutant, PASSED on restored code (shows the test is not false-green) | session log (not archived) |
| 2026-11-09 17:42 | 903e497 + perf tooling | tools/perf.sh latency measurement (synthetic volume) | see report; single machine, loopback, not a load test | `docs/performance/PERF_REPORT.md`, `perf-20261109T174206Z.json` |
| 2026-11-09 19:36 | 5bd8fa7 (deployed) | Playwright E2E against the LIVE Railway Demo deployment (https://web-demo-cd4b.up.railway.app, PostgreSQL on Railway, HTTPS via Railway edge) | 9/9 passed; axe 0 serious/critical on audited screens | `docs/evidence/railway-demo/e2e-live-20261109T193657Z.log`, `docs/evidence/railway-demo/screenshots/` |
| 2026-11-09 19:2x | 5bd8fa7 | E2E twice in a row on the same database (Demo container, local) — rerunnability | run 1 9/9; run 2 9/9 after the 1-minute login rate-limit window (an immediate rerun is correctly throttled: HTTP 429) | session log (not archived) |
