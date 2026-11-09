# Product requirements (living document)

**Product:** SERK Clinic OS — *One secure clinical foundation. Every specialty. Beautifully simple care.*
**Status:** Engineering draft derived from `docs/reference/architecture/` and `MASTER_BUILD_PROMPT.md`; requires product-owner and clinical-advisory approval.

## Actors
Receptionist, nurse, clinician (incl. dentist), clinic administrator, cashier, lab/pharmacy staff (future), SERK platform support (no routine PHI access), patient (future portal).

## Release tiers
1. **Foundation (Milestone A — in progress):** identity/tenancy, patient registry, scheduling, encounter + signed notes, versioned documents, audit, outbox, CI.
2. **Pilot clinical gate:** + prescriptions, basic billing, one specialty (Dental), backups/restore drill, OIDC+MFA, external security review.
3. **Multi-specialty gate:** Dermatology, OB/GYN, Pediatrics, labs, pharmacy/inventory, reports.
4. **Network readiness:** multi-facility reporting, hybrid sync, integrations (FHIR R4, NDHx where authorised).

## Implemented user journeys (synthetic data)
- J1 Receptionist registers a patient with duplicate review, books a clinician, confirms identity and checks in.
- J2 Clinician sees today's worklist, records allergy status, drafts with autosave/conflict detection, signs with allergy attestation, adds addenda.
- J3 Clinician prepares a consultation summary, previews (watermarked), finalizes (immutable PDF), downloads, opens the print dialog (physical print never assumed).
- J4 Anyone can verify a printed document's authenticity via QR without seeing patient data.

## Measurable outcomes (hypotheses, not verified)
Patient search p95 < 500 ms; encounter open p95 < 1 s on clinic LAN; RPO ≤ 15 min / RTO ≤ 4 h — **NOT MEASURED**.

## Non-goals (now)
Autonomous diagnosis or prescribing, AI note signing, microservices, generic form builder replacing typed clinical models, real-patient deployment.
