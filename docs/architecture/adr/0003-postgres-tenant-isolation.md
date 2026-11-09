# ADR-0003 PostgreSQL tenant isolation, hand-written module-owned SQL migrations
- **Status:** Accepted · **Date:** 2026-11-09
- **Decision:**
  - Every tenant-scoped table has `tenant_id`, `ENABLE` + `FORCE ROW LEVEL SECURITY`, policy `tenant_id = platform.current_tenant()`; context set per transaction via `set_config('app.tenant_id', …, true)` by `Platform.Data.DbSession` from the server-validated session affiliation. Missing context ⇒ NULL ⇒ zero rows (fail closed).
  - Application code ALSO filters by tenant and facility (RLS is defence in depth).
  - Runtime login is a member of `serk_app_role`: not owner, NOSUPERUSER, NOBYPASSRLS, least grants (no UPDATE/DELETE on signed/audit tables).
  - Schema is managed by ordered SQL scripts (`database/migrations/NNNN_*.sql`) applied by `DatabaseMigrator` (checksums, advisory lock). EF Core maps entities but never generates schema.
- **Why SQL not EF migrations:** RLS policies, grants, triggers and exclusion constraints are first-class and reviewable.
- **Risk:** EF model ↔ SQL drift (open: add model-vs-information_schema test). Identity directory tables are not RLS-protected (auth precedes tenant selection) — compensated by narrow grants; column-level grants open.
- **Evidence:** TenantIsolationTests (RLS fail-closed, pooled reuse, WITH CHECK), Runtime_role_is_least_privilege.
