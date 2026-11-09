# Module contracts (as implemented)

| Module | Schema | Owns | Published contracts (others may use) | Depends on (contracts) | Prohibited |
|---|---|---|---|---|---|
| SharedKernel | — | Result/Error, Capabilities, CanonicalJson, EthiopianDate, BusinessTime | all | none | any module/infra reference (tested) |
| Platform | `platform`, `audit` | DbSession (unit of work + RLS context), AuditTrail, OutboxWriter/Processor, IdempotencyStore, IObjectStore, DatabaseMigrator, endpoint authorization | infrastructure services | SharedKernel | business modules (tested) |
| Identity | `identity` | tenants, facilities, staff users, practitioners, affiliations, sessions, RoleCatalog | `IStaffDirectory` (practitioner/facility lookups) | Platform | — |
| Patients | `patients` | patients, MRN counters, allergies, duplicate matching | `IPatientDirectory` (`PatientBanner` incl. allergy review token) | Platform | other modules' internals (tested) |
| Scheduling | `scheduling` | appointments (exclusion constraint) | — | `IPatientDirectory`, `IStaffDirectory`, `IEncounterLauncher` | — |
| Clinical | `clinical` | encounters, drafts, immutable note versions | `IEncounterLauncher`, `ISignedNoteReader`, `IEncounterAccess`, `SignedNoteSnapshotV1`, `NoteContentV1` | `IPatientDirectory`, `IStaffDirectory` | PDF libraries (tested) |
| Dental (specialty pack) | `dental` | append-only FDI tooth findings attached to encounters, `DentalManifest` | — | `IEncounterAccess` (Clinical) only | Patients/Scheduling/Documents and Clinical internals (tested) |
| Documents | `documents` | document versions, template rendering, verification | `ConsultationDocumentSnapshotV1` | `ISignedNoteReader`, `IPatientDirectory`, `IStaffDirectory` | — |

**SQL-level coupling (deliberate, recorded in ADR-0001/0003):** cross-schema foreign keys for referential integrity
(e.g. `clinical.encounters.patient_id → patients.patients`) and `documents.verify_reference` joining `identity.facilities`
for the facility name. Module code never queries another module's tables.

**Events (outbox):** `clinical.note-signed.v1` `{ encounterId, noteVersionId }` (IDs only). Open: add `actor_id`,
`occurred_at`, `schema_version` in payload per 01_SYSTEM_ARCHITECTURE.

**Cross-module synchronous call:** check-in (Scheduling) starts the encounter via `IEncounterLauncher` in the same
transaction; Clinical verifies the caller holds `appointment.checkin`.

## Added 2026-11-09: Configuration, Analytics, automation

| Module | Schema | Owns | Publishes | Consumes |
|---|---|---|---|---|
| Configuration | `configuration` | facility settings (accent colour, default clinical paper A3/A4/A5, auto-prepare flag), branding assets (logo, stamp, per-practitioner signature; canonical metadata-free bytes + SHA-256), printer profiles (Standard/Thermal × paper × purpose) | `IBrandingReader` (`GetFacilityBrandingAsync`, `GetSignatureInEffectAsync(practitioner, signedAtUtc)`, hash-verified `ReadAssetAsync`) | `IStaffDirectory` |
| Analytics | — (reads PHI-free `security_invoker` views `scheduling.v_appointment_facts`, `clinical.v_encounter_facts`, `patients.v_registration_facts`, `documents.v_document_facts`) | none (read-only aggregates) | `GET /api/v1/analytics/overview`, `/daily.csv` | `IStaffDirectory` |

* Scheduling publishes `IAppointmentReader` (used by Documents for appointment slips).
* Documents freezes a `BrandingBlock` (accent, logo/stamp `AssetRef`, signatures in effect at signing) into the snapshot.
* Outbox event `clinical.note-signed.v1` payload is now `{ encounterId, noteVersionId, facilityId, actorUserId, occurredAt }`;
  consumed by `AutoPrepareSummaryHandler` (Documents), which only creates a watermarked draft.
* The fact views are the deliberate SQL-level read contract for analytics (ADR-0012); they expose no names, MRNs or
  free text, and RLS of the underlying tables applies to the caller (`security_invoker`).
