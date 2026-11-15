# Review round 5 — Care+ messaging, social media, 20 MB uploads (2026-11-15)

Independent AI security review (static reading; not a substitute for a human review). Status after fixes:

| ID | Severity | Finding | Status | Evidence |
|---|---|---|---|---|
| H1 | High | Telegram bot token (in the request path) would be written to HTTP client logs | Fixed: logging removed from that HTTP client | code (live send NOT RUN — no bot connected) |
| H2 | High | Four-eyes bypass: a clinician could rewrite a colleague's draft tip and approve it | Fixed: `last_editor_id`; approver must differ from author and last editor (service + DB CHECK); author immutable | CARE-006 |
| H3 | High | Sending 50 messages in one transaction: a non-HTTP failure rolled back already-sent messages → repeats | Fixed: one transaction per message; every non-shutdown error handled per message | CARE-006 (malformed provider answer) |
| H4 | High | Messages went to the address saved at planning even after phone change/unlink/relink | Fixed: address, channel and Telegram binding re-checked at send; unlink cancels queued Telegram messages and codes | CARE-006 |
| M1 | Medium | "Stop!", "please stop" not recognised as opt-out | Fixed: word-based opt-out for short replies; concern words take precedence | CareRulesTests |
| M2 | Medium | Campaigns reached other facilities' patients; no frequency cap | Fixed: facility-scoped audience; at most one tip per patient per 7 days | CARE-006 |
| M3 | Medium | Campaign cancel and replies/summary not facility-scoped | Fixed | code |
| M4 | Medium | Amharic symptom words missed (e.g. "እየደማሁ"); honest out-of-hours wording | Partly fixed: more stems; bot says "during clinic hours" + emergency advice. **Clinical review of word lists required**; no escalation of untouched tasks yet | CareRulesTests |
| M5 | Medium | Webhook shared the per-IP verify limit | Fixed: own generous bucket; unlinked chats get no answer | code |
| M6 | Medium | 20 MB images embedded in PDFs; wrong pixel message | Message fixed. Images are stored as uploaded (no downscaling library); large logos make every PDF large — **advise ≤ 1 MB logos**; downscaling is open work | code |
| L | Low | definer search_path, retired tip revival, webhook without chat id (500), follow-up STOP line | Fixed | code |
| L | Low | Link codes unsalted SHA-256 of 40 bits; shoulder-surfing of codes; SMS has no inbound STOP path | Accepted/documented (30-min single-use codes; reception confirms link; SMS sandbox only) | CARE_MESSAGING.md |

Open: human security review; clinical review of templates, word lists and tips; legal review of consent wording; real
channel tests (CARE-006 NOT RUN).
