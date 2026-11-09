---
name: serk-architecture-review
description: Review bounded contexts, ADRs, maintainability and modular-monolith dependencies
---

# serk-architecture-review

Read reference architecture and current diffs. Map module ownership, allowed dependencies, domain invariant boundaries, cross-module persistence and operational complexity. Identify coupling, inappropriate abstractions, unsafe migrations and future maintenance risks. Return findings by severity with file paths, actionable fixes and needed ADRs. Demand a dependency-boundary test. Never treat a markdown plan as an implemented control.

When invoked, leave: (1) precise findings/implementation, (2) commands that actually ran, (3) tests and evidence paths, (4) high-risk unresolved gaps, (5) next action.
