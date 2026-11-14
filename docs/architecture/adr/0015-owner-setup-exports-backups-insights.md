# ADR-0015: Owner setup, exports, organisation backups and explainable insights

Date: 2026-11-14 · Status: Accepted (engineering). Clinical, legal and independent security approval remain separate gates.

## Context
The owner asked for full control and setup of the clinic (logo, address, motto and other details), roles and
responsibilities, data exports "at all levels", backed-up data, a clearer interface and a "more intelligent" system.

## Decisions
1. **Clinic profile** lives in the Configuration module (`configuration.facility_profiles`), one versioned row per
   facility, edited with `If-Match` and audited with the list of changed fields. Documents **freeze** the letterhead in their
   snapshot (`branding.letterhead`, omitted when absent so older snapshots keep their hash); a later profile change never
   alters a prepared or issued document. Slips use the current profile.
2. **Owner role (policy v4)** gets every administrative, finance, configuration and data capability of the organisation —
   including staff session control, finance close and prices, exports, patient-data export and backups — but **still no
   clinical-record capability**. Changing who holds which role stays with super administrators (two-person-rule decision
   still open, ADR-0013). Cash collection stays with cashiers (separation of duties).
3. **Responsibilities** are stated per built-in role in `RoleCatalog` and shown with the permission matrix to every
   member (transparency); the team list needs `staff.admin`.
4. **Exports:** one gate (`ExportService`) for all datasets; each module declares its own datasets (`ExportDefinition`
   with parameterised SQL run under the request's row-level security). Modules read each other only through published
   views (`patients.v_patient_reference`, `identity.v_staff_reference`). Patient-identifying datasets need
   `data.export.patient`, a written purpose (10–300 chars, DB CHECK) and password re-confirmation. Limits: 366 days,
   100 000 rows, per-user rate limit. CSV is UTF-8 with BOM and neutralises spreadsheet formulas. Every export is logged
   (`platform.export_log`, append-only) with its SHA-256 in the same transaction as its audit event.
5. **Organisation backup** in the app (encrypted, owner-held, verifiable) plus an operator restore rehearsal script; see
   `docs/operations/BACKUP_RESTORE.md`. Platform reads the tenant's tables by name from a catalogue that must cover every
   organisation table (enforced by test) — a deliberate, documented exception to "modules read only their own tables",
   because a backup must be complete and must not depend on each module remembering to contribute.
6. **"More intelligent" = explainable rules, not machine learning.** `InsightRules` turns PHI-free facts into insights
   with severity, the numbers, and the rule text ("Why am I seeing this?"). Thresholds are constants with unit tests.
   No clinical judgement, no prediction about individual patients, nothing sent to an AI service.
7. **Interface:** navigation grouped by purpose (Care, Money, Insight, Administration, Me), a keyboard command palette
   (Ctrl/⌘+K) that lists only what the person may open, a setup checklist with progress, and a live letterhead preview.

## Consequences
- Owners can configure, export and back up their organisation without a developer; every sensitive act is attributable.
- Patient-data export by an owner is now possible (purpose + password + log). Whether a non-clinical owner may export
  patient registers is a **legal/privacy decision for the clinic** (data-protection law); it can be removed per person
  with a per-assignment revocation.
- Not built: in-app restore, scheduled/off-site backups, MFA for step-up, export of clinical note content (use issued
  PDFs), localisation of the new screens' text (English only for now).
