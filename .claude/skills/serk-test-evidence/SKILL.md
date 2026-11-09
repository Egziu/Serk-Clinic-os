---
name: serk-test-evidence
description: Design test matrix and run reproducible evidence-backed QA gates
---

# serk-test-evidence

Trace requirements → code → test case → outcome → evidence. Run real compiler, integration tests, E2E, authorization, concurrency, failure injection, property tests, performance where applicable and dependency security checks. Report command, environment, timestamp, exit code, pass/fail/skip totals, coverage and evidence paths. Never mark an unavailable test as passed; do not relax failures to claim green.

When invoked, leave: (1) precise findings/implementation, (2) commands that actually ran, (3) tests and evidence paths, (4) high-risk unresolved gaps, (5) next action.
