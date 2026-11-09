# SERK Clinic OS — Architecture Kit v1.0

**Prepared:** 9 November 2026  
**Status:** Design proposal, not implemented or clinically validated.  
**Purpose:** Establish a secure, maintainable C# clinic platform usable by single-specialty practices, multidisciplinary clinics, and chains.

## The non-negotiable decision

Start with a **modular monolith** on **C# / .NET 10 LTS**, ASP.NET Core APIs, PostgreSQL, React + TypeScript (or Blazor as a deliberate single-stack alternative), private object storage and background workers. Do not begin with many distributed microservices. Do not use dynamic clinical form schemas as a replacement for clinically validated domain models. Do not put identifiable patient data in analytics or application logs.

## Kit contents

1. `01_SYSTEM_ARCHITECTURE.md` — deployment topology, boundaries, data flow, diagrams, reliability.
2. `02_MODULES_AND_SPECIALTIES.md` — shared modules, specialty packages and clinical workflows.
3. `03_DATA_AND_API_DESIGN.md` — entities, integrity, tenant isolation, API conventions and event patterns.
4. `04_SECURITY_PRIVACY_COMPLIANCE.md` — threat model, clinical safety, Ethiopian legal considerations and audit.
5. `05_UX_DOCUMENTS_PRINTING.md` — clinician workflows, visual system, PDF/preview/sign/print design.
6. `06_OPERATIONS_TESTING_ROADMAP.md` — deployments, backup/restore, testing, acceptance gates and staged delivery.
7. `07_ENGINEERING_RULES_AND_REPO.md` — repo boundaries, code conventions, decision records and maintenance.

## Essential qualifications

- All clinical forms, care pathways, prescriptions, patient-safety warnings and clinical decision support require review and acceptance by qualified healthcare professionals.
- Legal compliance and clinical device/clinical decision support regulation require jurisdiction-specific professional review.
- A clinic's signed clinical note is legally/operationally different from an editable draft. Use formal amendments, not silent edits.
- Proposed performance, recovery and availability measures in this kit are **engineering targets**, not verified results.
- FHIR mappings, ICD-11, DICOM and terminology choices are external interoperability features, not a guarantee of conformance.

## Authoritative starting points

- Microsoft .NET support: https://dotnet.microsoft.com/en-us/platform/support/policy
- HL7 FHIR R4: https://hl7.org/fhir/R4/
- WHO ICD-11: https://www.who.int/news/item/16-02-2026-icd-11-2026-release
- LOINC: https://loinc.org/about
- DICOM: https://www.dicomstandard.org/
- OWASP ASVS 5.0: https://asvs.owasp.org/
- Ethiopia Personal Data Protection Proclamation No. 1321/2024: https://justice.gov.et/en/law/personal-data-protection-proclamation/
- Ethiopia Ministry of Health NDHx: https://interop.moh.gov.et/
- SNOMED CT licensing: https://www.snomed.org/licensing
