# Tool capability register — 2026-11-09 (Claude Code cloud session)

Sanitized; no secrets. "Used" means actually used in this session.

| Tool / capability | Version / source | Trust | Connectivity (observed) | Use in project | PHI exposure | Status |
|---|---|---|---|---|---|---|
| git | 2.43.0 (Ubuntu) | OS package | GitHub via session proxy | VCS, push to `claude/modest-pascal-x3qzg9` | none | Used |
| .NET SDK | 10.0.114 (Ubuntu noble-updates, source-built) | Distro package (Microsoft download host blocked by proxy, 403) | NuGet.org reachable | build/test | none | Used |
| Node / npm | 22.23.4 / 10.9.9 | preinstalled | registry.npmjs.org reachable | web build/lint/test | none | Used |
| PostgreSQL | client 16.15; server `postgres:16.6-alpine` (Docker Hub) | official image | local container | dev + integration tests | synthetic only | Used |
| Docker | 29.9.2 (daemon started manually) | preinstalled | Docker Hub reachable | Testcontainers, dev DB | synthetic only | Used |
| Playwright + Chromium | 1.63.0 (preinstalled browsers) | preinstalled | local | E2E + axe | synthetic only | Used |
| poppler-utils | Ubuntu | distro | — | manual PDF inspection | synthetic only | Used |
| fonts-noto-core | 20201225-2 Ubuntu | distro, OFL-1.1 | — | vendored PDF fonts | none | Used |
| Kit subagents (`.claude/agents/*`) | build kit v1 | project-supplied, read-only | — | Charters given to 3 independent reviewer agents | synthetic code only | Used (via general-purpose agents; kit agents load at next session start) |
| Kit skills (`.claude/skills/*`) | build kit v1 | project-supplied | — | Followed as written guidance | — | Available at next session start |
| GitHub MCP | session-provided | Anthropic-managed | scoped to `egziu/serk-clinic-os` | not needed yet (push via git) | none | Available, unused |
| Other MCP servers (Supabase, Vercel, Railway, Sentry, Gmail, Drive, Notion, Figma, etc.) | session-provided | third-party | various | **Not used — irrelevant or would export data / incur cost** | — | Deliberately unused |
| ICD-10 / NPI / CMS MCP | session-provided | third-party | — | Not used (US-specific; ICD-11 is the target) | — | Unused |
| Many plugin MCPs | — | — | **failed to connect (proxy 403)** | — | — | Unavailable |
| Datadog MCP | — | — | requires authentication | — | — | Unavailable |
| Secret scanner (gitleaks/trufflehog) | — | — | not installed | — | — | **Unavailable — SEC-006 NOT RUN** |
| SAST (semgrep/CodeQL) | — | — | not installed locally; CodeQL could be added to CI | — | — | **NOT RUN** |
| `/doctor`, `/plugin`, `/mcp` interactive commands | — | — | not invokable from this non-interactive session | — | — | Not verified |
