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

## Branding uploads, printing, automation and analytics (added 2026-11-09)

| Threat | Example | Controls implemented | Evidence | Residual / open |
|---|---|---|---|---|
| Malicious image upload | HTML/JS polyglot, SVG script, decompression bomb, CMYK/palette edge cases | Allow-list PNG (8-bit RGB/RGBA, non-interlaced) and baseline/progressive JPEG; 512 KB, 16–4096 px, ≤ 4 MP; PNG CRCs + pixel data inflated with a hard bound to exactly the declared size; full JPEG marker walk to EOI (≤ 32 scans); no trailing bytes; **stored bytes are a canonical re-serialisation without any metadata/comment chunks**; served only as sniffed type with nosniff and `no-store`; rejections logged as security events | BRAND-002, BRAND-007 | No full pixel decode/re-encode (structure-level canonicalisation only); no AV scanning |
| Information disclosure via image metadata | EXIF GPS, author, XMP in a logo/signature, copied into patient PDFs | All APPn (except a standard Adobe APP14), COM, tEXt/iTXt/zTXt/eXIf and other ancillary chunks dropped before storage | BRAND-007 | — |
| Signature forgery / misuse | Colleague downloads another clinician's signature; signature applied to notes signed before it existed | Signature content only to its owner (not branding managers); `no-store`; snapshot freezes only the signature **in effect at signing time**; preview never shows it | BRAND-004, BRAND-005 | Any reader of a final PDF can extract the embedded image — **legal decision pending** on signature images in patient copies |
| CSRF on multipart | Cross-site form upload | Session CSRF header required (antiforgery middleware disabled only in favour of the existing header check) | BRAND-003 | — |
| Race / integrity | Parallel uploads; duplicate outbox delivery creating two drafts | Transaction-scoped advisory locks per asset slot and per note version | BRAND-006, AUTO-001 | Object written before commit can be orphaned on rollback (storage GC open) |
| Automation privilege | Outbox handler acting as a user | System actor limited to `document.create/read`, new DI scope per message, tenant from the event, audited | AUTO-001 | — |
| Cross-tenant background job | No-show sweeper | Per-tenant transaction with transaction-local RLS context and explicit tenant predicate; failures isolated per tenant; grace ≥ 30 min enforced | AUTO-002 | Sweeper bypasses the Scheduling aggregate and emits no domain event (open) |
| Re-identification via analytics | 1-day range shows a single registration by sex/age | `security_invoker` PHI-free fact views; demographic counts 1–4 suppressed; range bounded (2000…today+1y, ≤ 366 days); audited | ANALYTICS-002 | Heatmap counts not suppressed (operational, no demographics); complementary disclosure not modelled; no per-user rate limit on analytics/uploads (open) |
| Timing side channel | Server-Timing on sign-in | Header omitted on `/api/v1/auth/*` and `/api/v1/verify/*`; 0.1 ms resolution elsewhere | HARDEN-001 | — |

## System super administration (added 2026-11-10, ADR-0013)

| Threat | Example | Controls implemented | Evidence | Residual / open |
|---|---|---|---|---|
| Super admin reads patient data | "Control the whole system" used as a PHI back door | Platform role grants only `/api/v1/platform/*`; no actor/tenant context; responses contain no patient data | ACCESS-002 | A super admin can create a second account and give it clinical access (audited, not dual-controlled) — **two-person rule open** |
| Self-escalation | Admin edits own assignment, revives own revoked status | Self-modification refused on every account/access route; step-up for super-admin changes | ACCESS-005/006 (mutation-checked) | — |
| Account takeover of a super admin | Phished password | 15-min idle timeout, lockout, rate limit, step-up for admin changes | ACCESS-006, HARDEN-002 | **No MFA (IDENT-001)** — blocker for any real deployment |
| Cross-organisation role injection | Use org B's custom role in org A | Roles resolved only within the assignment's own organisation; codes namespaced `custom.*`; unknown roles grant nothing | ACCESS-007, AccessPolicyTests | — |
| Hidden or deniable access changes | Silent edits, log tampering | Change log written in the same transaction; append-only trigger even for the owner; security events | ACCESS-009 | DB superuser can disable triggers (as for audit) |
| Stale or temporary credentials | Admin-set password reused | Forced change before any access; reset ends sessions; password policy | ACCESS-003/008 | Temporary password is shown once in the browser; secure hand-over is procedural |
| Broadened runtime DB grants | Injection writing identity tables | Column-limited INSERT/UPDATE only, no DELETE; all SQL parameterised (EF/Npgsql) | migration 0011 | Identity tables have no RLS (by design, ADR-0003) |

## Attackers considered
Internet users (anonymous verify endpoint, login), curious insiders (search, duplicate probe — now audited), rogue tenant
(tenant B staff), compromised workstation (session revocation), malicious tools/plugins (no PHI to tools; synthetic data
only), CI compromise (pinned SDK/lockfiles, locked restore; signed artifacts **not yet**).

## Not yet in scope (explicitly)
Malware/AV scanning of uploads, payment webhooks, break-glass, MFA/OIDC, backup encryption & key recovery, DAST, container
scanning, SBOM publication (license inventory exists: docs/security/THIRD_PARTY_NUGET.md).
