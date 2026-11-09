# Initial audit — 2026-11-09

**Repository at start:** one commit, `README.md` only. No code, tests, CI, migrations or ADRs existed. The architecture kit
was supplied as an upload and imported (`CLAUDE.md`, `MASTER_BUILD_PROMPT.md`, `.claude/`, `docs/reference/architecture/`).

**Environment:** see `TOOL_CAPABILITY_REGISTER.md`. .NET 10 had to be installed from Ubuntu packages because the Microsoft
download host was blocked; Docker daemon was started manually; external MCP plugins mostly unavailable.

**Key risks identified up front:** tenant isolation (multi-tenant clinical data), signed-record immutability, wrong-patient
workflows, PDF licensing (confirmed: PDFsharp ≥ 6.3 is AGPL), vulnerable transitive dependencies (confirmed: Pkcs 8.0.1),
absence of an identity provider (MFA cannot be delivered in-session), no backup infrastructure.

**First iteration (executed):** Milestone A skeleton + vertical slice staff login → registration → appointment →
encounter → note → signature → preview → final PDF, with real PostgreSQL, RLS, audit, outbox, idempotency, tests and E2E.

**Acceptance criteria used:** builds with zero warnings; all automated suites pass with no skips; release-critical IDs for
the slice have automated evidence; independent reviews run and verified findings fixed with regression tests.
