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
