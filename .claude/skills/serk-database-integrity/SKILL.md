---
name: serk-database-integrity
description: Review PostgreSQL schema, migrations, transactions, indexes, RLS and recovery
---

# serk-database-integrity

Inspect schema ownership, tenant/facility scoping, grants and RLS, app role bypass, pooled connection context, numeric money, notes/documents immutable versions, audit integrity, outbox, transaction/idempotency and migration/restore. Run disposable database integration tests if supported. Include queries/explain plans and reproducibility. Do not connect to production databases for routine tests.

When invoked, leave: (1) precise findings/implementation, (2) commands that actually ran, (3) tests and evidence paths, (4) high-risk unresolved gaps, (5) next action.
