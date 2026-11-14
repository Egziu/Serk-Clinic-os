# Care+ Finance pack v2.0 — status against the pack's acceptance checklist

Source: `docs/reference/expansion-v2/ACCEPTANCE_AND_DELIVERY_CHECKLIST.md`. Evidence: `docs/evidence/finance/verify-20261114T171809Z.log`,
`docs/evidence/finance/e2e-20261114T171809Z.log`. Synthetic data only. Passing tests are not an accounting, clinical or legal approval.

## Finance and close
| Item | Status | Evidence / note |
|---|---|---|
| Double-entry posting with versioned mappings; debit = credit enforced | PASS (automated) | FIN-LEDGER; database triggers |
| No duplicate receipts on callback replay | PASS (automated) | FIN-A |
| Invoice → payment → reconciliation → close → report trace in UI and API | PASS (automated) | FIN-A, FIN-CLOSE, e2e/finance.spec.ts |
| Authorization for discounts, refunds | PASS (automated) | FIN-REFUND |
| Price overrides, cashier transfers, adjusting journals | NOT IMPLEMENTED | |
| Cash count vs expected, variance with separate approval | PASS (automated) | FIN-B |
| Close blocks unresolved mandatory differences | PASS (automated) | FIN-C |
| Conditional exceptions labelled and approved | NOT IMPLEMENTED | |
| Locked snapshots immutable | PASS (automated) | FIN-CLOSE, FIN-LEDGER |
| Late corrections as linked supplemental versions | NOT IMPLEMENTED | later postings fall into the next business day |
| Branch cutoffs and cross-midnight shifts | NOT IMPLEMENTED | business date = facility-local calendar date |
| GL, AR, clearing and revenue independently recalculated | PARTIAL | trial balance and clearing checked in tests; no independent recalculation |
| Accountant sign-off | NOT RUN | default chart is illustrative |

## Owner reports and alerts
| Item | Status | Evidence / note |
|---|---|---|
| Recipient verified by channel challenge | PASS (automated) | FIN-E |
| Recipient portal MFA / step-up on changes | NOT IMPLEMENTED | no MFA in the system yet (IDENT-001) |
| Telegram private chat only | PARTIAL | group chats refused in FIN-E; no real Telegram connector exists |
| Digest has no PHI or secrets | PASS (automated) | FIN-CLOSE asserts digest content |
| Report states facility, business day, currency, close version, status | PASS (automated) | FIN-CLOSE |
| Accepted / retry / dead-letter states | PASS (automated) | FIN-D (sandbox channel) |
| Repeated worker events do not duplicate | PASS (automated) | FIN-CLOSE, FIN-D |
| Correction notices for corrected closes | NOT IMPLEMENTED | |
| Failed dispatch leaves close intact and visible | PASS (automated) | FIN-D |

## Patient luxury and follow-up
All seven items NOT IMPLEMENTED. The pack requires clinical protocol review before Care+ follow-up is built.

## Quality gates
| Item | Status | Evidence / note |
|---|---|---|
| Build, unit, integration tests executed, logs kept | PASS | 142 unit, 24 architecture, 112 integration, 0 failed |
| Browser tests | PASS | Playwright 15/15, axe 0 serious/critical on finance screens |
| Migration from empty database | PASS | migration 0012 applied in every integration run |
| Migration rollback rehearsal | NOT RUN | |
| Cross-tenant and privilege tests for finance | PASS (automated) | FIN-LEDGER (tenant isolation), FIN-E, FIN-REFUND |
| Backup restore with balanced ledger verified | NOT RUN | |
| Static analysis, dependency scan | PASS | analyzers as errors; NuGet/npm audit 0 known vulnerabilities |
| Secrets scan, independent security review of finance | NOT RUN | |
| Mutation checks of the finance tests | NOT RUN | |
| Go-live authorisation | NOT RUN | demo only |

## Known issue
`APPT-002` (concurrent booking, existing test, code unchanged) failed once in `tools/verify.sh` and once in four
targeted reruns on 2026-11-14 with HTTP 500 instead of 409 for the losing requests. It then passed 27 consecutive runs
(3 full suites, 24 targeted) and the cause was not found. The test now reports the server error if it recurs.
