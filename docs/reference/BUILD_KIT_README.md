# SERK Clinic OS — Claude Code Build and Verification Kit

**Version 1.0 — 9 November 2026.** This is an **engineering prompt and Claude Code project configuration package**, not a built or clinically certified clinic application.

## Included

- `MASTER_BUILD_PROMPT.md` — 5,800+ word execution mandate; paste into Claude Code or ask Claude to read it.
- `CLAUDE.md` — concise project-level persistent rules.
- `.claude/skills/` — 12 focused skills for architectural, specialty, quality, security and operational engineering.
- `.claude/agents/` — 7 independent read-only reviewer subagents (they do not replace qualified clinicians/security professionals).
- `docs/reference/architecture/` — eight original SERK Clinic OS blueprint files.
- `docs/testing/` — sample risk-based release gates.
- `docs/progress/` — honest status template.
- `examples/` — optional, **not autoenabled**, hook example.

## Installation / use

1. Create a **private** repository `serk-clinic-os`, or work inside your existing authorized private repo. Back up any existing `CLAUDE.md`, `.claude/` or `docs/` before merging this kit; do not overwrite user-authored files blindly.
2. Copy the **contents** of this kit into the repo root while preserving the `.claude` hidden directory.
3. In your terminal: `cd serk-clinic-os` followed by `claude`. Use Claude Code in a properly authorized development environment with .NET 10, Node, Docker/Podman and PostgreSQL tooling as applicable.
4. Ask Claude: **`Read MASTER_BUILD_PROMPT.md, CLAUDE.md and docs/reference/architecture; inventory tools and the repository, then begin Milestone A and the first complete vertical slice. Use relevant trusted skills and subagents, execute tests, produce evidence, and stop before any production/destructive action.`**
5. Check skills using Claude Code's available commands (`/help`, `/doctor`, `/mcp`, `/plugin`, skill listing as shown by your installed CLI); actual availability varies by version/environment. MCP servers are **not bundled**; connect only approved servers with least privilege.
6. Use the release-gate review after every milestone. Only actual logs and test results count.

## Recommended sequence

1. Audit architecture/source/tool trust, threat model and project wiring.
2. Build and test registration → appointment → encounter → sign → PDF with authorization and real PostgreSQL.
3. Deliver Dental as first specialty reference; extend Dermatology, OB/GYN and Pediatrics, each with clinical-review blockers.
4. Expand billing, pharmacy, lab/imaging, print studio, analytics and resilience only in tested vertical slices.
5. Independent professional clinical/security/legal reviews before real patient deployment.

## Trust and safety

This bundle deliberately excludes auto-installing plugins and silently connecting to any cloud or live clinic database. No real patient data in Claude Code, MCP tools or test fixtures unless formally permitted under the clinic's lawful, approved data handling program. Never call automated tests regulatory or clinical certification. Understood limitations and approvals must be explicit.

## Authoritative documentation links

- Claude Code skills: https://code.claude.com/docs/en/skills
- Claude Code plugins: https://code.claude.com/docs/en/plugins
- Claude Code MCP: https://code.claude.com/docs/en/mcp
- Claude Code subagents: https://code.claude.com/docs/en/sub-agents
- Claude Code hooks: https://code.claude.com/docs/en/hooks
- .NET support: https://learn.microsoft.com/en-us/dotnet/core/releases-and-support
- OWASP ASVS 5.0: https://asvs.owasp.org/
