# ADR-0005 Clinical record lifecycle: draft → signed immutable version → attributed addenda
- **Status:** Accepted (engineering; clinical content PENDING CLINICIAN REVIEW) · **Date:** 2026-11-09
- **Decision:** One editable draft per encounter (ETag/If-Match optimistic concurrency, autosave). Signing (attending practitioner, valid licence, active affiliation, allergy review token match, required fields) writes an immutable `note_versions` row containing the canonical JSON snapshot (`SignedNoteSnapshotV1`) and its SHA-256, plus audit and outbox in the same transaction. Corrections are addenda (new versions referencing the prior one, mandatory reason). Signed encounters cannot be reopened (DB trigger).
- **Evidence:** ClinicalRecordTests, ReviewRound1RegressionTests, unit ClinicalDomainTests.
- **Open:** entered-in-error/void workflow (HAZ-002), attending reassignment (HAZ-018), patient identity inside the signed snapshot (HAZ-017).
