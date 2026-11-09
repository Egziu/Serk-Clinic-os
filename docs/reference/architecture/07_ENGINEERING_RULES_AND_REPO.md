# 07 — Engineering Rules, Repository Shape and Maintenance

## Reference repository layout

```text
serk-clinic-os/
├── README.md
├── docs/
│   ├── architecture/
│   │   ├── system-context.md
│   │   ├── module-boundaries.md
│   │   ├── deployment-profiles.md
│   │   └── adr/
│   ├── clinical-safety/
│   ├── security/
│   ├── api/
│   └── runbooks/
├── src/
│   ├── backend/
│   │   ├── Serk.Clinic.Api/
│   │   ├── Serk.Clinic.Host/
│   │   ├── Serk.Clinic.SharedKernel/
│   │   ├── Serk.Clinic.Identity/
│   │   ├── Serk.Clinic.Patients/
│   │   ├── Serk.Clinic.Scheduling/
│   │   ├── Serk.Clinic.Clinical/
│   │   ├── Serk.Clinic.Orders/
│   │   ├── Serk.Clinic.Pharmacy/
│   │   ├── Serk.Clinic.Billing/
│   │   ├── Serk.Clinic.Inventory/
│   │   ├── Serk.Clinic.Documents/
│   │   ├── Serk.Clinic.Audit/
│   │   ├── Serk.Clinic.Integrations/
│   │   └── Serk.Clinic.Worker/
│   ├── specialties/
│   │   ├── Serk.Clinic.Dental/
│   │   ├── Serk.Clinic.Dermatology/
│   │   ├── Serk.Clinic.Gynecology/
│   │   └── Serk.Clinic.Pediatrics/
│   └── web/
├── tests/
│   ├── Unit/
│   ├── Integration/
│   ├── Contracts/
│   ├── Security/
│   ├── ClinicalAcceptance/
│   └── EndToEnd/
├── infrastructure/
│   ├── containers/
│   ├── migrations/
│   ├── monitoring/
│   └── deployment/
└── tools/
```

## Inside each business module

```text
Serk.Clinic.Dental/
├── Domain/              # Entities, rules, invariants, state machines
├── Application/         # Commands/queries, policies, orchestration, DTOs
├── Infrastructure/      # Persistence, adapters, migrations
├── Contracts/           # Public module interfaces/events
├── Api/                 # Versioned endpoint mappings
└── Tests/               # Unit, integration and module acceptance
```

Avoid shared layers that let every module call every other module’s repository. The SharedKernel should contain only a few genuinely universal primitives (IDs, outcome types, business time, money); never use it as a dumping ground for unrelated utilities.

## Dependency rules

- Domain cannot reference EF Core, ASP.NET, UI or external gateways.
- Application depends on Domain and public service abstractions.
- Infrastructure implements interfaces and owns persistence/messaging adapters.
- API calls Application through authenticated/authorized commands.
- Specialty code references published core contracts only; not direct core implementation internals.
- DB migrations are module-owned, ordered and reviewed; changes to another module's schema require coordination.

## Code review rules

- Every new endpoint: authorization policy, failure semantics, validation, audit and tests.
- Every change to clinical forms: clinical reviewer, terminology/reference version, backward compatibility and clinical safety scenarios.
- Every migration: non-production rehearsal, rollback/restore plan, expected lock and data migration impact.
- Every background task: idempotency, retry semantics, observability and dead-letter process.
- Every document: versioning, data classification, access policy and preview/final consistency.
- No untracked third-party packages, embedded secrets, unencrypted local storage or PHI in build logs.
- No 'catch all and return success', no silent deletion of medical history, no direct intermodule table access.

## Decision records (ADRs)

Create small decision files with context, options, decision, trade-offs, consequences, date, owner, revisit trigger. The first ADRs should cover: (1) modular monolith, (2) React vs Blazor, (3) PostgreSQL tenant isolation, (4) cloud/on-prem/hybrid, (5) clinical record lifecycle, (6) template rendering, (7) FHIR interoperability profile, (8) offline and sync policies, (9) security key management, (10) patient identity and merge policy.

## Governance and staffing

Keep one clinical safety lead and a reviewer for each specialty, a product owner, backend/DB owner, frontend owner, QA/security lead and operations/support owner. Individuals can cover multiple roles on a small team, but responsibilities and approvals must remain distinct for high-risk changes. Clinical requirements must be validated with real users rather than inferred solely from demonstrations or competitor products.

## Maintenance commitments

- Pin .NET LTS major version and patch monthly, with regression testing; plan an LTS upgrade before support expiry.
- Use semantic product versioning and additive database migrations; keep old clients compatible during rollout window.
- Separate all clinic customizations into versioned configuration and template packages, never code forks.
- Maintain smoke tests, tenant boundary tests, print golden files, clinical safety regressions and recovery rehearsals.
- Monitor dependency vulnerability advisories; keep an asset inventory, SBOM and clear incident contact path.
- Publish release notes, user help, administrator runbook, clinical known limitations and a verified rollback path.
