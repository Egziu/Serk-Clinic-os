# Threat model — SERK Clinic OS (scope: Milestone A vertical slice)

**Status:** Engineering threat model written by the implementing agent and challenged by one independent AI security
reviewer (docs/reviews/2026-11-09-review-round-1.md). **Not** an independent professional assessment; an external
penetration test is a mandatory gate before any real-patient pilot.

## Assets
Patient identity and demographics; allergies and clinical notes (signed versions are legal/clinical records); finalized
PDFs and their hashes; audit trail; staff credentials and sessions; tenant boundaries; signing authority.

## Trust boundaries
1. Browser ↔ API host (HTTPS in production; same-origin SPA; cookie session).
2. API host ↔ PostgreSQL (runtime role `serk_app` member of `serk_app_role`: non-owner, NOBYPASSRLS).
3. Migrator (schema owner) ↔ PostgreSQL (deployment-time only).
4. API host ↔ private object store (filesystem in dev/clinic-local).
5. Anonymous internet ↔ `/api/v1/verify/{ref}` (opaque reference, no PHI).
6. Outbox worker ↔ (future) external integrations.

## STRIDE by boundary (selected; controls reference code)

| Threat | Example | Controls implemented | Evidence | Residual / open |
|---|---|---|---|---|
| **S**poofing | Stolen password; session theft | PBKDF2 (ASP.NET Core PasswordHasher), atomic lockout, generic errors, timing equalisation, opaque random session token (only SHA-256 stored), HttpOnly+Secure+SameSite=Strict `__Host-` cookie, idle 30 min / absolute 12 h, logout + admin revocation | AuthTests, ReviewRound1 lockout test | **No MFA (IDENT-001 NOT IMPLEMENTED)**; local password store only — ADR-0004 requires OIDC IdP + MFA before real data |
| **T**ampering | Edit a signed note; alter a final PDF; re-point encounter; rewrite audit | No UPDATE/DELETE grants on note versions/audit; immutability triggers block even the owner; SHA-256 of canonical snapshot verified on every read; PDF hash verified on download; encounter identity freeze | ClinicalRecordTests.Signed_note_is_immutable, DocumentTests.Tampered_object_is_not_served, Final_document_row_is_frozen | A DB superuser can still disable triggers → needs WORM audit copy / external hash anchoring (open) |
| **R**epudiation | Clinician denies signing | Signature binds practitioner id, credential text, UTC time, content hash; audit with session + correlation id | Authorized_sign, Sensitive_read_audited | Correlation ID may be client-supplied (open SEC-R1-11); no cryptographic (PKI) signature — clinician attestation only |
| **I**nformation disclosure | Cross-tenant IDOR; search leakage; logs; QR | App-level tenant+facility predicates on every query; RLS (FORCE) fail-closed with transaction-local context; 404 not 403 for foreign objects; minimal search projection; PHI-free logging policy (exception type only); QR holds opaque ref; PDFs never via public URL; `Cache-Control: no-store` | TenantIsolationTests, Logs_do_not_contain_phi, Supersede_and_verify | Identity directory tables readable by runtime role (open); outbox table without RLS (open) |
| **D**enial of service | Login flood; huge bodies | Per-IP rate limit (login, verify); 1 MB body cap enforced server-independently; pagination caps | AuthTests, Request_limits | Per-IP limits collapse behind a proxy without ForwardedHeaders (open) |
| **E**levation of privilege | Reception signs; admin reads notes; replay another user's idempotent result | Capability checks per endpoint; ABAC (facility, attending clinician, licence validity, active affiliation); clinic admin has no clinical capability; support account has no affiliation; idempotency replay bound to actor; mass-assignment guard (unknown JSON members rejected) | Unauthorized_sign_refused, Clinic_admin_cannot_read_charts, Support_has_no_phi_access, Idempotency_replay_bound_to_actor, Unknown_json_members_rejected | Break-glass not implemented (SEC-005 NOT IMPLEMENTED) |

## Attackers considered
Internet users (anonymous verify endpoint, login), curious insiders (search, duplicate probe — now audited), rogue tenant
(tenant B staff), compromised workstation (session revocation), malicious tools/plugins (no PHI to tools; synthetic data
only), CI compromise (pinned SDK/lockfiles, locked restore; signed artifacts **not yet**).

## Not yet in scope (explicitly)
Uploads/malware scanning, payment webhooks, break-glass, MFA/OIDC, backup encryption & key recovery, DAST, container
scanning, SBOM publication (license inventory exists: docs/security/THIRD_PARTY_NUGET.md).
