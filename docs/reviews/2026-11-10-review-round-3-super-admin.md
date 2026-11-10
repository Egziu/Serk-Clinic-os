# Review round 3 — system super administration (2026-11-10)

Independent read-only AI security review of the super administrator feature (not a human or certified review).

| ID | Severity | Finding | Disposition | Evidence |
|---|---|---|---|---|
| F1 | Critical | One super admin can reach PHI or act as a clinician indirectly: create an account (or a second super admin) with a known temporary password and give it clinical access, or reset an existing clinician's password and sign in as them. All logged, nothing prevents it. | **OPEN — owner decision.** Recommended: two-person approval for clinical/patient-data grants, practitioner profiles, super-admin grants and resets of clinical/admin accounts; out-of-band single-use reset links instead of admin-chosen passwords; no clinical assignment on super-admin accounts. Until then: synthetic data only. | ACCESS-014 NOT IMPLEMENTED |
| F2 | High | Concurrent cross-revocation could leave zero super admins; deactivated accounts counted | **Fixed**: transaction + advisory lock; only active accounts count | ACCESS-010 (mutation-checked: 5/5 fail without lock) |
| F3 | Medium | Runtime DB role can now write identity tables; an SQL injection anywhere could grant super admin silently | **Open**: recommended separate DB role or SECURITY DEFINER functions + trigger requiring a same-transaction change-log row. No injection found (all SQL parameterised). | migration 0011 |
| F4 | Medium | Step-up password could be guessed without limit | **Fixed**: failures count towards lockout; at 5 the account locks and sessions end | ACCESS-011 |
| F5 | Medium | Lost updates on assignments/roles | **Fixed**: EF concurrency tokens; version required; 412 | ACCESS-012 |
| F6 | Medium | Demo super admin shared the common demo password | **Fixed**: separate `Seed:SuperAdminPassword` (≥ 16, ≠ demo password), forced change at first sign-in in Demo; none seeded without it | SerkHost, DemoDataSeeder |
| F7 | Low | Built-in role names reusable; self-unlock; reactivation restored admin; unique-race 500; reason may hold PHI | **Fixed** (names, unlock, reactivation, 409, UI hint). Organisation list unbounded — open (small scale). | ACCESS-013 |
