---
name: serk-security-red-team
description: Threat model and adversarially test clinic patient access, authorization, API, files and infrastructure
---

# serk-security-red-team

Review OWASP ASVS 5.0 applicability and threat model; test tenant/facility BOLA/IDOR, role escalation, RLS connection reuse, sensitive searches, signing, print/export, upload, SSRF/XSS/CSRF, malicious PDF and webhook replays on authorized synthetic test targets. Never attack non-owned systems. Run tests/scanners where available; cite actual tool evidence and scope. Mark unrun checks NOT RUN. Critical/high exploitable weaknesses block release.

When invoked, leave: (1) precise findings/implementation, (2) commands that actually ran, (3) tests and evidence paths, (4) high-risk unresolved gaps, (5) next action.
