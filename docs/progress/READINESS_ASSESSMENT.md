# Readiness assessment — "Is SERK Clinic OS ready for Ethiopian dental clinics?" (2026-11-15)

## Verdict
| Use | Status |
|---|---|
| Demonstration, training, staff walkthroughs with **synthetic** data | **GREEN** |
| Supervised pilot with synthetic or test records in one clinic | **GREEN** with the conditions below |
| Real patients and real money | **RED — not yet.** 21 release-blocking items are not implemented or not run, and no clinical, legal, accounting or independent security approval exists |

A claim such as "the best system for Ethiopian dental clinics" cannot be made from tests: it needs real-clinic use, clinician
and owner feedback, comparison with alternatives, and the approvals below. Code tests prove behaviour, not suitability or compliance.

## What was checked (all PASS, 2026-11-15)
- tools/verify.sh: locked restore, formatting, Release build with 0 warnings, licence gate, NuGet/npm vulnerability audits
  (0 known), 197 unit + 26 architecture + 132 integration tests on PostgreSQL 16, 21 web unit tests.
- Playwright: 23/23 end-to-end flows (registration → booking → check-in → note → sign → PDF; dental chart; finance day close;
  owner setup, exports, encrypted backup + verify; Care+; social media; owner adds staff and custom roles; super admin),
  automated accessibility scans (axe, WCAG 2.2 A/AA) with 0 serious/critical findings on audited screens.
- Restore rehearsal: full database dump restored into a fresh database — identical row counts (57 tables), migrations,
  balanced ledger, 26 protection triggers and 43 row-level-security tables preserved.
- Release-critical scenarios: **83 of 104 release-blocking scenarios PASS** (automated); 5 independent AI security reviews
  with all High findings fixed or explicitly accepted.

## Strengths for Ethiopian dental clinics (implemented and tested)
Ethiopian and Gregorian dates side by side on screens, slips and messages; ETB finance with double-entry ledger, cash
drawers, mobile-money sandbox and daily close; FDI dental chart with wrong-tooth confirmation; signed, immutable notes with
verifiable PDFs (QR); clinic letterhead with logo, stamp, motto, licence and TIN; Care+ reminders and follow-up by SMS or
Telegram (sandbox); owner tools (staff, roles, exports, encrypted backups, insights); multi-branch organisations.

## Must be done before real patients (priority order)
1. **Clinical safety sign-off** — 42 hazards, none reviewed by a licensed clinician; dental workflows, follow-up word
   lists and health tips need a dentist's review.
2. **Login security** — two-factor sign-in (IDENT-001) and the two-person rule decision (ACCESS-014).
3. **Backups in production** — continuous database backups with off-site copies and a signed restore drill (OPS-003,
   BACKUP-004/005). Today only in-app encrypted exports and a local rehearsal exist.
4. **Data protection and hosting** — legal review against the Personal Data Protection Proclamation No. 1321/2024 (consent
   wording, patient rights, cross-border transfer rules). The demo runs on Railway in a US region; real patient data may
   need hosting in Ethiopia or an approved arrangement.
5. **Amharic and Tigrinya interface** — the language architecture exists but screen text is English (I18N-001); patient
   messages have Amharic/Tigrinya drafts that need native review.
6. **Dental essentials still missing** — treatment plan with cost estimate and consent (DENT-004), prescriptions (MED-002),
   dental X-ray/image attachments, patient record merge (PAT-002).
7. **Finance and tax** — accountant approval of the chart of accounts; VAT/TOT and receipt/invoice rules of the Ethiopian
   revenue authority (not implemented — no guessed tax rates).
8. **Independent human security test** (penetration test), secret scanning (SEC-006), break-glass access (SEC-005).
9. **Operations** — behaviour during internet outages (OPS-005), upgrades with preserved records (OPS-002), real SMS/
   Telegram connection with owner approval (CARE-006, FIN-012).

Details: docs/testing/RELEASE_CRITICAL_SCENARIOS.csv, docs/clinical-safety/HAZARD_REGISTER.csv, docs/reviews/.
