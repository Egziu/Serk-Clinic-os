# Test evidence index

| Date (UTC) | Commit | What | Result | Evidence |
|---|---|---|---|---|
| 2026-11-09 14:38 | f5e7c21 | Release build; unit + architecture + integration | build 0 warnings/0 errors; 51 + 15 + 53 passed, 0 failed, 0 skipped | `docs/evidence/milestone-A/build-20261109T143855Z.log`, `test-20261109T143855Z.log` |
| 2026-11-09 14:59 | (review round 1 fixes, see BUILD_STATUS) | Release build; unit + architecture + integration; E2E; NuGet/npm audit | 0 warnings; 53 + 15 + 64 passed, 0 failed, 0 skipped; E2E 4/4 passed; 0 known vulnerable packages | `docs/evidence/milestone-A/*-20261109T145933Z.log`, `screenshots/` |

TRX files are produced under `artifacts/` (git-ignored) and uploaded by CI. Screenshots contain synthetic data only.
CI workflow `.github/workflows/ci.yml` is **authored but not yet observed running** on GitHub.
