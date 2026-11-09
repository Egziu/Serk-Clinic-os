# 04 — Security, Privacy, Clinical Safety and Compliance

## Threat model

Adversaries include malicious internet users, compromised employee accounts, insiders abusing patient searches, phishing, infected clinic workstations, ransomware, malicious uploaded files, fraudulent refunds, lost laptops, insecure lab/payment integrations and development pipeline compromise. Assets include protected patient records, medical images, prescribed medicines, clinical decisions, financial transactions and signing keys.

## Defense layers

1. **Identity:** independent identities for staff and patients; OIDC-compliant identity provider; MFA mandatory for privileged roles and strongly recommended for all clinical users; device/session control, login anomaly detection, managed account recovery.
2. **Authorization:** combine RBAC (receptionist, doctor, nurse, lab, pharmacy, cashier, clinic admin, SERK support) with ABAC (assigned facility, active employment, treatment relationship, specialty, consent, sensitivity, time, explicit emergency override).
3. **Tenant scoping:** authorized tenant context in middleware and application services; Postgres RLS as backup isolation, with automated cross-tenant tests.
4. **Encryption:** TLS 1.2+ or TLS 1.3 with current policies; at-rest database, backup and object encryption; keys separated from encrypted data and centrally rotated; never embed secrets in code.
5. **Endpoint security:** managed workstations, disk encryption, OS patching, short lock timeout, printer and USB policy, no clinical content in browser caches unless approved local offline architecture.
6. **Application security:** OWASP ASVS 5.0 as the verification baseline, threat modeling, authorization tests, SAST/SCA/secret scanning, external pen test before broad launch.
7. **Operational security:** least privilege in deployment, private DB network, backups with restricted restore access, incident runbooks, alerting and security reviews.
8. **AI controls:** clinician-in-the-loop, no automatic diagnosis/prescription/signature, protected data only to approved providers under documented terms and legal basis; prompt injection and model leakage controls.

## Suggested access policy matrix

| Action | Reception | Nurse | Treating clinician | Lab | Cashier | Clinic admin | SERK support |
|---|---|---|---|---|---|---|---|
| Register patient | Y | Scoped | Scoped | N | N | Scoped | N |
| View demographics | Scoped | Scoped | Scoped | Minimal | Minimal | Scoped | N |
| View detailed clinical notes | N | Care-based | Care-based | N | N | Exceptional governed | N |
| Write clinical note | N | Role-limited | Y | N | N | N | N |
| Sign diagnosis / orders | N | N | Privileged clinician | Role-specific lab result | N | N | N |
| Create invoice / receive payment | N | N | N | N | Scoped | Configured | N |
| Refund / change charges | N | N | N | N | Initiate only | Approve if permitted | N |
| User administration | N | N | N | N | N | Scoped | Platform operations only, no PHI |

These are default policy examples; permissions should be implemented as fine-grained capability checks rather than hard-coded role names. Facility admin power is **not** automatic blanket chart access. SERK support access must be time-limited, justified, explicitly approved and audited, with no routine PHI access.

## Emergency break-glass

When a care-critical need exists and ordinary authorization blocks access, allow limited emergency override only where legally and clinically permitted: mandatory reason and patient linkage, stronger authentication, expiry, conspicuous audit, automatic privacy officer review and retrospective investigation. Never use break-glass as a normal workaround.

## Audit requirements

Append-only event trail for create/read/update/export/print and permission changes on sensitive clinical objects; security events separately indexed. Record actor, patient/resource ID, reason, facility, event type, server time, outcome and source metadata. Keep clinical content out of audit log messages. Apply tamper-evidence or WORM retention to critical audit copies. Protect logs against ordinary tenant admins rewriting them; test retrieval during incident simulation.

## Privacy compliance design

Ethiopia has Personal Data Protection Proclamation No. 1321/2024, and healthcare-specific regulations and professional obligations may also apply. Treat medical data as highly sensitive. Before a production launch, legal counsel and clinical governance must determine controller/processor responsibilities, legal bases, patient notices, rights workflows, retention, processor agreements, localization and cross-border transfer requirements, consent scenarios, incident obligations and approval pathways. Consult primary text, not just a checklist: https://justice.gov.et/en/law/personal-data-protection-proclamation/

Separate **clinical consent**, **privacy notice/lawful processing basis**, **communications preference**, **photo authorization**, **research consent** and **marketing consent**. They are not interchangeable. Do not grant access to a patient record merely because they previously consented to an unrelated service.

## Clinical safety engineering

- Maintain hazard log, known limitations, clinical owner and sign-off per specialty and release.
- Validate medication rules, pregnancy calculations, normal ranges, growth charts and clinical forms against current authoritative clinical guidance and approved local practice.
- Display measurement units clearly; reject ambiguous medication units and unverified device values.
- Clearly differentiate an unsigned draft, signed note, ordered test, collected specimen, verified result and acknowledged critical result.
- Use warnings with defined severity and escalation, not arbitrary colorful notifications. Ensure clinically important alerts can be tracked to resolution.
- Require qualified clinician final signoff; no hidden AI modifications of a chart.
- Never present predictive scores or image analysis as reliable without appropriate validation and regulatory evaluation.

## Release-blocking security tests

- Cross-tenant and cross-facility patient object ID access.
- Mass assignment, IDOR, privilege escalation, hidden-field tampering.
- Session revocation, stolen device and MFA recovery.
- Malicious upload, document rendering and stored XSS.
- SQL injection, concurrency collisions, race-condition double billing/dispensing.
- Data leakage through search autocomplete, SMS, PDF, presigned URL, support access and telemetry.
- Ransomware recovery drills, encryption key restore and backup integrity.
- Emergency break-glass misuse, chart amendments and read audit completeness.
