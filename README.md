# SERK Clinic OS

*One secure clinical foundation. Every specialty. Beautifully simple care.*

A multi-tenant, multi-specialty clinic platform: C# / .NET 10 modular monolith, PostgreSQL 16 with row-level security,
React + TypeScript. **Status: early engineering build with synthetic data only — not clinically validated, not legally
reviewed, not production approved.** See [`docs/progress/BUILD_STATUS.md`](docs/progress/BUILD_STATUS.md).

## What works today (automatically tested)
Staff login (revocable sessions, lockout, CSRF) → patient registration with duplicate review → appointment booking
(race-safe) and identity-confirmed check-in → encounter with autosaved draft → authorised signature (immutable, hashed,
allergy-attested) → attributed addenda → consultation summary preview (watermarked) → final PDF (same renderer and
snapshot, write-once, hash-verified, QR verification without PHI). Two-tenant isolation enforced in code and by PostgreSQL RLS.

## Layout
```
src/backend/      SharedKernel, Platform, Identity, Patients, Scheduling, Clinical, Documents, Host
src/web/          React + TypeScript SPA (Vite), Playwright E2E
database/         ordered, module-owned SQL migrations (RLS, grants, immutability triggers)
tests/            unit, architecture (module boundaries, license guard), integration (real PostgreSQL)
docs/             requirements, architecture + ADRs, security, clinical safety, testing, operations, evidence
```

## Quick start (development)
See [`docs/operations/INSTALL.md`](docs/operations/INSTALL.md). Tests: `dotnet test` (Docker or `SERK_TEST_PG_ADMIN`),
`npm test` in `src/web`, `tools/e2e.sh` for end-to-end.

## Governance
Project rules: [`CLAUDE.md`](CLAUDE.md), [`MASTER_BUILD_PROMPT.md`](MASTER_BUILD_PROMPT.md). Clinical, legal and
independent security approvals are separate human gates and have **not** been obtained.
