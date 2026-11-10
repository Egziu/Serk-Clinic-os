# ADR-0013 System super administration, custom roles and per-assignment permissions
- **Status:** Accepted (engineering); security owner review PENDING · **Date:** 2026-11-10
- **Context:** The owner asked for a super administrator who controls the whole system and can give any role or permission,
  with deep customisation. The existing model had fixed roles per affiliation and no administration API.
- **Decisions:**
  1. **Platform role, not a clinic role.** `identity.platform_admins` marks super administrators. It unlocks only
     `/api/v1/platform/*` (organisations, facilities, people, practitioner profiles, assignments, roles, super admins, change log).
     It grants **no** tenant or patient data: clinical access still needs an ordinary affiliation, and a super administrator
     cannot give one to themselves.
  2. **Customisation model.** Built-in roles stay in code (`RoleCatalog`, the tested safety baseline: e.g. clinic admin and
     owner have no clinical capability) and cannot be edited, only cloned. Organisations get custom roles
     (`identity.role_definitions`, code `custom.*`) built from a described capability catalogue (`CapabilityCatalog`, with
     sensitivity: operational / patient data / clinical / administrative). Each assignment can add or remove individual
     capabilities and has a validity window. Effective = (roles ∪ granted) − revoked, resolved on every request, so changes
     apply immediately without re-login.
  3. **Guardrails.** No self-modification of account or access; never fewer than one active super administrator; step-up
     password for super-admin grant/revoke; super-admin idle timeout 15 min; created/reset accounts must change the
     temporary password before anything else; deactivation and reset end sessions; warnings for risky combinations
     (clinical + administrative, signing without a practitioner profile); every change written to the append-only
     `identity.admin_changes` (before/after JSON, reason, correlation id) in the same transaction.
  4. **Database grants.** The runtime role gains column-limited INSERT/UPDATE on identity tables (no DELETE anywhere;
     deactivate instead). Authorisation is enforced in the Identity module; identity tables remain outside tenant RLS
     because authentication precedes tenant context (unchanged from ADR-0003).
- **Not decided here / open:** MFA for super administrators (IDENT-001 — required before any real data); a two-person rule
  for granting clinical access (today one super administrator can create an account and give it clinical access, which is
  audited but not dual-controlled); tenant-scoped delegated administration for clinic admins; break-glass access (SEC-005).
- **Evidence:** RELEASE_CRITICAL_SCENARIOS ACCESS-001..009, HARDEN-002; unit `AccessPolicyTests`; E2E `super-admin.spec.ts`;
  mutation checks recorded in TEST_EVIDENCE_INDEX.
