---
name: serk-release-gate
description: Audit release readiness and prevent promotion without evidence and human approvals
---

# serk-release-gate

Produce an evidence-indexed release checklist: reproducible build, test totals, critical workflow matrix, dependency scans, zero unresolved critical/high exploitable issues, migration/restore, document consistency, perf, real UAT, external clinician, legal and independent security approval. Mark each Approved, Failed, Not Run, Pending External Approval, or Not Applicable with justification. Never merge or deploy without user authorization.

When invoked, leave: (1) precise findings/implementation, (2) commands that actually ran, (3) tests and evidence paths, (4) high-risk unresolved gaps, (5) next action.
