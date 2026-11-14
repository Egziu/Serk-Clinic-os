# Claude Code — SERK Clinic OS v2 Care+ / Finance Expansion Mandate

Read the accompanying SRS v2 and its appendices, then read original SERK Clinic OS architecture and Claude Code master build prompt in the same repository. Inspect the actual code, installed tools, repository permissions, tests, environment and MCP capabilities. Preserve working code and document incompatibilities. Use only trusted relevant skills, agents and plugins with least privilege.

**Your mission is to build tested functional code, not a feature checklist or mock dashboard.** Begin with finance source integrity and a complete vertical slice linking service → invoice → verified payment → receipt → ledger → reconciliation → daily close → owner digest. Continue into the luxury patient journeys and follow-up engine.

## Mandatory architectural choices

- C#/.NET 10 modular monolith; PostgreSQL; explicit module interfaces and separate schema ownership.
- Decimal money, stable IDs, versioned accounting mappings, balanced journal, immutable posted entries and reversals.
- Event outbox committed with business state; idempotent workers and provider callback handling.
- Confidentiality-preserving, MFA-authorized owner subscriptions; private Telegram chat binding by challenge, verified email, channel retry evidence and no PHI in owner messages.
- Facility-local, timezone-aware configurable business cutoff; close checks; immutable snapshot and safe correcting adjustments.
- Clinician-approved aftercare and service recovery; no autonomous clinical diagnosis or closure.
- React/TypeScript responsive premium UX with accessibility; authorized preview, print and PDF history.

## Deliverables at every milestone

Production-quality code, migration scripts, API contract, UI, positive and negative unit tests, database integration tests, E2E flows, security cases, operational telemetry, backup/restore evidence, documentation and honest build status. Never report NOT RUN as PASSED.

## Release blockers

Failed tenant isolation; PHI leakage; unbalanced posted GL; duplicate payment posting; unsafe financial reversal; missing/false close evidence; unauthorized recipient; untraceable funds; unapproved clinical aftercare; backup restore failure; and unreviewed security weaknesses.

## Immediate first step

Inventory repo and build tools; produce an implementation diff against the SRS; construct a requirement traceability matrix; then implement a correctly tested invoice/payment/posting and source-trace path with fictional fixtures. Do not access live finances, patients or production messaging until explicitly authorized.
