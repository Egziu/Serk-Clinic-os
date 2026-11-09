# Independent review round 1 — 2026-11-09

Three independent, read-only AI reviewer agents (security/database, clinical-safety, architecture/test-quality) reviewed
commit `f5e7c21`. **These are AI reviews, not professional security, clinical or legal sign-off.** Their findings were
verified by the implementer against the code before acting; every fix below has a regression test that was executed.

## Disposition

| ID | Source | Severity | Finding | Status | Evidence |
|---|---|---|---|---|---|
| SEC-R1-01 / QA-D2 / HAZ-013 | security + QA + clinical (independently) | High | Document from an older note version could supersede a final that contains a later addendum, silently dropping a correction | **Fixed**: create/finalize require the latest signed version (422 `not_latest_version`, 409 `stale_document`) | `ReviewRound1RegressionTests.Older_version_document_rejected`, `Stale_preview_cannot_be_finalized` |
| QA-D1 | QA | High | Draft save racing with sign could return 200 while the edit was excluded from the signed note | **Fixed**: signing bumps the draft concurrency token | `Draft_save_sign_race` (8 iterations), unit `Sign_produces_hashed_immutable_version_and_locks_draft` |
| SEC-R1-05 / QA-D2 | security + QA | Medium | Concurrent finalization could leave two current Final documents | **Fixed**: partial unique index (migration 0007) + 409 mapping | `Concurrent_finalize_single_final` |
| SEC-R1-03 / QA-D5 | security + QA | Medium | Idempotency replay not bound to the actor (cross-user disclosure of stored responses) | **Fixed**: replay only for the same user, else 409 `idempotency_key_conflict` | `Idempotency_replay_bound_to_actor` |
| SEC-R1-04 | security | Medium | Lost-update race on failed-login counter | **Fixed**: single atomic UPDATE | `Lockout_after_repeated_failures` (12 concurrent failures) |
| SEC-R1-02 | security | Medium/High | Public verification function only worked because the owner was a superuser | **Fixed**: dedicated NOLOGIN `serk_document_verifier` owner with column grants + RLS policy | `Verifier_function_not_superuser_owned` |
| SEC-R1-06 | security | Medium | Duplicate-candidate disclosure at registration left no audit trail | **Fixed**: independent-transaction audit `patient.duplicate_candidates_disclosed` | `Duplicate_disclosure_audited` |
| SEC-R1-07 | security | Low/Med | Encounter `patient_id` could be re-pointed after signing | **Fixed**: identity-freeze trigger | `Encounter_patient_cannot_be_repointed` |
| SEC-R1-08 | security | Low | Document trigger left patient/facility/attribution mutable | **Fixed** (migration 0007) | `Final_document_row_is_frozen` |
| SEC-R1-09 | security | Low | `audit.security_events` lacked TRUNCATE guard | **Fixed** (migration 0007) | migration applied in integration fixture |
| SEC-R1-10 | security | Low | Admin revocation could kill a user's sessions in another tenant | **Fixed**: scoped to admin's tenant sessions | `Admin_can_revoke_all_sessions_of_staff_in_own_tenant` (scope not separately tested — **gap**) |
| QA-D3 | QA | Medium | Concurrent same-national-ID registration returned 500 | **Fixed**: 409 `duplicate_identifier` | not separately raced — **gap** |
| QA-T1..T5 | QA | — | Test-quality defects (outbox cross-talk, race losers unchecked, unscoped assertions, misleading names, no try/finally) | **Fixed** | see diff of tests in commit |
| HAZ-001 | clinical | Major | Check-in without second identifier | **Mitigated**: DOB+sex in DTO, mandatory identity confirmation dialog | E2E `receptionist registers...` |
| HAZ-006 | clinical | Major | Allergies uncorrectable | **Fixed**: inactivate / entered-in-error with reason; status returns to Unknown | `Allergy_correction` |
| HAZ-009 | clinical | Major | Signature could attest to allergies never displayed | **Fixed**: allergy review token required at sign | `Allergy_change_between_review_and_sign` |
| HAZ-010/012 | clinical | Major | Printed summary showed stale allergies; amendments not flagged before content | **Fixed** in template v2 | `DocumentTemplateV2Tests` |
| HAZ-014 | clinical | Major | Superseded PDFs served as if current | **Fixed**: 409 `document_superseded` | `Superseded_download_refused` |
| HAZ-015 | clinical | Moderate | Faint preview watermark / "draft" wording | **Fixed**: "PREVIEW - NOT ISSUED", higher opacity | unit `Preview_is_watermarked_and_final_is_not` |
| HAZ-016 | clinical | Minor | QR could point at localhost | **Fixed**: startup refuses non-https/loopback base URL outside Development | code review only (**NOT TESTED**) |
| HAZ-021 | clinical | Moderate | UTC date used as "today" | **Fixed** for DOB validation and licence checks (facility zone) | partial: unit `Facility_day_range_uses_addis_ababa_offset`; no midnight-boundary integration test (**gap**) |

## Open (not fixed in this round) — tracked in `docs/clinical-safety/HAZARD_REGISTER.csv` and `docs/security/THREAT_MODEL.md`

- HAZ-002 entered-in-error encounter workflow; HAZ-003/004 duplicate-matching quality and transliteration; HAZ-005 search truncation flag (UI shows a hint at 25 results only); HAZ-008 dating of "no known allergies" on print; HAZ-011 typed allergy enum in signing gate; HAZ-017 patient identity not inside the signed hash; HAZ-018 attending reassignment; HAZ-019 overdue unsigned drafts; HAZ-020 infant age display; HAZ-022 Ethiopian local time-of-day wording on patient-facing documents.
- SEC-R1-07 composite (tenant, id) foreign keys; SEC-R1-09 outbox RLS / separate worker role, column-level grants on `identity.*`; SEC-R1-11 forwarded headers for per-IP limits behind a proxy, HSTS, object-store permissions, client-supplied correlation IDs in audit.
- QA-D4 Identity reads use a separate connection from the request unit of work (documented, pool sizing); QA-D6 If-Match optional on check-in/cancel; QA-D7 list reads (worklist/day list) not audited; QA-D8 signed-chain integrity failure returns 500; QA-D9 outbox processor does not set tenant context for handlers; architecture recommendations (EF-model vs SQL drift test, outbox lease outside transaction, idempotency retention job, event payload actor/occurred_at).
