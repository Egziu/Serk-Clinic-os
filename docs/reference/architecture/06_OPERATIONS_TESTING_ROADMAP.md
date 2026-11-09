# 06 — Operations, Quality Gates and Delivery Roadmap

## Release strategy

Use milestone-based acceptance, not a purely calendar-driven go-live promise.

### Foundation gate

- Product requirements and risk assessment approved by SERK and a clinical advisory group.
- Identity, permissions, tenant/facility model, patient registry, audit, migrations, build pipeline, infrastructure, backups established.
- Threat model and minimum incident procedures written.

### Pilot clinical gate

- Appointment, reception, encounter, notes, prescriptions, basic billing, documents, records search and print operational.
- One specialty pack end-to-end; dental is a good pilot because it exercises the anatomical chart and treatment plan.
- Real clinicians run structured scripts on synthetic patients.
- All blocker/critical defects closed; restore drill completed; security review passed.

### Multi-specialty gate

- Dermatology and gynecology packs reviewed; specialty forms, photographic consent, high-risk obstetric workflows evaluated.
- Laboratory/order handling, pharmacy/inventory and configurable reports added according to clinic need.
- Cross-specialty reuse verified: a patient’s allergy list and medications are shared correctly.

### Network readiness gate

- Multi-facility administration and reporting; robust offline/edge profile if justified; authorized national/interclinic integrations.
- Formal availability, recovery and security targets established from production-like load and fault tests.
- Customer onboarding, upgrade paths, SLA/support agreements and tenant exit/export tested.

## Test pyramid

- Unit: value objects, state transitions, medication units, money, permission logic, calculation logic.
- Integration: Postgres persistence, RLS and policies, transaction outbox, object storage access, PDF renderer, payment adapters.
- Contract: API schemas, integration versioning, medical device feeds and patient data export mapping.
- E2E: receptionist -> physician -> prescription -> cashier -> print; specialist cases; cancellation/refund; outage recovery.
- Security: SAST, dependency scans, dynamic checks, pen tests, negative role and tenant access tests.
- Clinical: scenario-by-scenario sign-off with qualified professionals, including red-flag, critical result and high-risk medication conditions.
- Resilience: power/WAN outage, duplicate retries, node failure, restored backup, sync conflicts, partial payments.
- Usability: observation of actual staff performing high-frequency tasks without developer assistance.

## Operational metrics and thresholds

Choose realistic thresholds during pilots, then enforce them in CI or operational gates where measurable: p95 API latency, error rate, lost update incidents, unsigned encounters older than expected, failed critical result acknowledgements, print failures, billing discrepancies, backup restore time, alarm-to-response time and unauthorized access findings. No dashboard metric replaces clinical outcomes review.

## Backup and disaster recovery

- Automated encrypted PostgreSQL base backups plus WAL archiving for point-in-time recovery.
- Independent encrypted object store backups and verification; consistent manifests linking database records to documents.
- Periodic copy to an appropriately protected independent fault domain; retention and immutability options to mitigate ransomware.
- Encrypted secrets/keys recovery documented separately; never back up database data without required keys.
- Restore a fresh isolated environment regularly and compare record counts, file hashes, signed document ability, representative clinical workflow and reporting consistency.
- Formal RPO/RTO targets signed by business owners; RPO <= 15 minutes and RTO <= 4 hours are possible **pilot targets only**, dependent on actual tests and investment.

## Cloud / local rollout

- Cloud: region-residency and provider security review, private networks, WAF/reverse proxy, managed database, object store, secrets/KMS, encrypted backups, logging and monitoring.
- On premises: UPS, lockable server, daily backup rotation, drive encryption, firewall, supervised updates, disk monitoring and failover/support plan.
- Roll forward/rollback: migration compatibility, blue/green or staged deploys where feasible; destructive migrations prohibited without tested data preservation.
- Every production deployment has an identifiable release, change record, signed artifact and post-deploy smoke-test result.

## Acceptance criteria for a clinic pilot

1. Staff member can register a patient and identify a duplicate without disclosing other tenant's data.
2. Receptionist can book/check in; clinician receives correct encounter.
3. Clinician can record allergies and critical fields, issue valid orders and sign a note.
4. The system blocks unauthorized signing and silent edit of signed note.
5. A prescription and invoice are correctly scoped and printed from final immutable versions.
6. A clinic-admin cannot read a sensitive chart merely by using admin menus.
7. Two staff competing to alter the same appointment or invoice cannot cause silent loss or double payment.
8. Clinic resumes safely from a simulated failure; backup and object restore verified.
9. Printer/network failure produces a recoverable error and no false 'printed successfully' claim.
10. Security, clinical, usability and compliance owners sign the launch checklist.
