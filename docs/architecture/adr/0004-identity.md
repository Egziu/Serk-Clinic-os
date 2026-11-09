# ADR-0004 Identity: local credential sessions now, OIDC + MFA before real patients
- **Status:** Accepted for synthetic environments only · **Date:** 2026-11-09
- **Decision:** Server-side revocable sessions with opaque cookie tokens, lockout, CSRF header token; capability-based authorization with versioned role catalog (`RoleCatalog.Version = 1`).
- **Mandatory before real-patient pilot:** federate to a supported OIDC identity provider (e.g. Keycloak / Entra ID) with phishing-resistant MFA for privileged and clinical roles (IDENT-001). Local passwords become break-glass/offline only after review.
- **Consequences:** IDENT-001 is a release blocker (NOT IMPLEMENTED).
