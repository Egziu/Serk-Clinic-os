# 02 — Modules, Specialty Packs and Workflows

## Shared core: module acceptance checklist

| Module | Essential workflows | Safety/financial constraints |
|---|---|---|
| Patient Registry | register/search, duplicate review, authorized merge, contacts, guardians | never use name alone as primary identifier; preserve merge history |
| Front Desk | appointment, waitlist, check-in, triage queue, no-show | prohibit double booking unless approved override |
| EMR | problem list, allergies, history, vitals, note drafts, signed notes | sign/lock/amend; attributable updates; configurable privacy restrictions |
| Orders | lab, imaging, procedure and referral orders | ordered vs collected vs resulted vs acknowledged are distinct states |
| Medications | medicine directory, medication request, dispense, discontinue | allergies, interaction notices and dose support validated by clinicians |
| Lab | specimen receipt, result entry, verification, critical communication | critical result notification and acknowledgement record |
| Imaging | images, reports, attachments, viewer | clinical imaging is DICOM where supported; original metadata retained |
| Finance | price book, deposits, invoicing, receipts, insurer workflow, refunds | posted transactions not casually deleted; reversal and ledger trails |
| Inventory | lots, expiration, stock location, procurement, consumption | dispensing and stock reductions reconciled; expired item warning |
| Documentation | preview, letterhead, PDF, signatures, controlled print/export | exact version match, provenance and audit |
| Access/HR | affiliations, role/permission, leave, roster, credential expiry | sensitive clinical permission separate from finance privilege |
| Analytics | operational dashboards and quality indicators | role-scoped aggregation and minimum necessary disclosure |
| Configuration | branch, service catalog, languages, tax/invoice settings | versioned configuration, staged publication, approval on high-risk changes |

## Specialty contract

Every specialty pack defines:

1. `SpecialtyManifest`: ID, version, minimum platform version, supported workflows, localization keys.
2. `EncounterExtensions`: typed structured examination values referencing a core encounter.
3. `FormDefinitions`: approved fields, units, validation, conditional visibility, revisions and provenance.
4. `ClinicalAssets`: anatomical visualizations, equipment associations, photo capture.
5. `ProcedureCatalogue`: procedures, supply consumption, billing links and contraindication flags for specialist review.
6. `DocumentTemplates`: consult report, consent, procedure note, aftercare and follow-up.
7. `PolicySet`: authorized clinician roles, sensitive-chart policy, cross-specialty review rules.
8. `Tests`: schema/migration, clinical acceptance, accessibility and security regression.

Specialty packages must **not** redefine `Patient`, `Encounter`, `MedicationRequest`, `Invoice` or `Document` tables. They attach stable specialist records using a 1:1 or 1:many relationship with the owning encounter.

## Dental and orthodontics

- Tooth numbering: FDI/ISO tooth notation, primary and adult dentition.
- Interactive odontogram with surface-level findings and treatment status; SVG vector interface with accessible nonvisual alternatives.
- Periodontal chart: sites, depths, bleeding, mobility, recession.
- Clinical documentation: caries, endodontics, prosthodontics, periodontal treatment, implant and orthodontic planning.
- Treatment plans: phased visits, accepted cost estimate, consent, material lot, stage completion and future appointment.
- Photos and radiographs securely linked to encounter/tooth, with DICOM when supported.
- Dedicated dental note and print layout.

## Dermatology

- Full-body front/back maps with lesion markers and measurement history.
- Body-site semantics; diagnostic photos with normalized exposure/time/angle metadata.
- Consent for capture, separate consent and rules for teaching or marketing use.
- Chronological before/after comparison with explicit clinical context; never imply automated diagnostic accuracy from images alone.
- Procedure notes for biopsy, cryotherapy and clinically selected interventions.
- Longitudinal treatment response, side-effect checks, follow-up and escalation.

## Gynecology and obstetrics

- Reproductive and menstrual history, parity and gravidity, dates and dating-source traceability.
- Gyn examination form, screening and results, ultrasound/referral links.
- Antenatal record, longitudinal visits, gestational-age calculation with clinician verification, blood pressure and laboratory trends.
- Approved risk alerts and follow-up plans with clinician-controlled acknowledgement; no autonomous clinical management.
- Restricted access for highly sensitive notes, guardian or companion privacy controls appropriate to local law and care policy.
- Consent documentation; maternal visit summary and referral letter.

## Pediatrics

- Child profile linked to guardians and authorized consent relationships.
- Age-dependent measurements, percentile/growth charts based on validated references.
- Immunization records and schedules configurable to current national protocols.
- Weight-based prescribing assistance gated by clinical verification, renal/age restrictions, dose range checks.
- Developmental follow-up, safeguarding flags and access restrictions.

## Additional packs

- Ophthalmology: visual acuity, IOP, refraction, slit-lamp findings, eye diagrams.
- ENT: hearing studies, otoscopy and nasal/laryngeal examination diagrams.
- Orthopedics: joint maps, ROM, injury timeline, imaging and rehabilitation.
- Cardiology: ECG, echo reporting, monitoring and treatment follow-up.
- Psychiatry: structured assessments, especially restricted records and non-stigmatizing workflows.
- Physiotherapy: mobility measures, care plans, visit attendance and outcome tracking.
- General/family medicine: problem-oriented notes, chronic disease registries and preventive care.

## Clinical visit state machine

`Booked -> Arrived -> Triaged -> In Consultation -> Ordered/Treated -> Awaiting Result (optional) -> Reviewed -> Signed/Closed -> Follow-up`.

Transitions are constrained; for example a closed encounter cannot have a prior signed note silently replaced. Reopen must be an audited, permission-controlled action with a reason and amendment handling.

## Example dental workflow

Registration -> appointment -> check-in -> dentist opens chart -> reviews allergy/problem list -> odontogram/exam -> adds structured diagnosis/findings -> discusses plan -> captures consent -> selects procedures -> issues orders/prescriptions if needed -> signed note -> invoice -> receipt -> patient receives aftercare and follow-up printout. Every stage has a stable business object and explicit owner.
