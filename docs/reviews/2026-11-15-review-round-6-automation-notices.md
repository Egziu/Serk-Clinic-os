# Review round 6: automation, recalls and notice board (2026-11-15)

The review was an independent read-only security review of migration 0015, NoticeService, RecallService, CareAutomation,
the new endpoints, the role grants and the notice board page. It found no Critical or High issues, no SQL injection and
no cross-tenant leak.

| # | Severity | Finding | Outcome |
|---|---|---|---|
| F1 | Medium | A manager could read receipts of, or take down, a notice belonging to another branch (receipts and archive looked the notice up by id only). | **Fixed.** Both are now limited to the caller's branch or organisation-wide notices. Regression in NOTICE-001: 404 for another branch. |
| F2 | Medium | Recall invitations reused appointment-reminder consent. | **Fixed.** Added a separate `consent_recalls` setting, **off by default**, so no existing patient receives check-up invitations without explicit agreement. It is recorded in consent history and audit, withdrawing it cancels queued invitations, STOP clears it, and it is re-checked at send time. Regression in RECALL-001. |
| F3 | Low | The patient-data guard misses bare MRN numbers and names. | **Accepted.** The guard is a safety net, not a control. The UI warns staff; HAZ-044 notes the need for training. |
| F4 | Low | An automatic notice taken down by hand returns on the next run while its problem persists. | **Accepted by design.** Automatic notices show only while the problem exists, and resolving the problem removes them. |
| F5 | Low | Closing another branch's recall answered "already closed" instead of "not found". | **Fixed.** Now returns 404. Regression in RECALL-002. |

## Found by our own testing in the same round
- Automatic notices stopped for any clinic that had taken a backup: a timestamp was read as the wrong type and the
  error was swallowed. Fixed, with a regression test that was shown to fail on the old code.
- An archived automatic notice could never be re-posted. Fixed: the notice key is retired when it is archived.
- An author could not see their own notice when it was addressed to other roles. Fixed.
- A hovered primary button became unreadable (pale background with white text), across the whole app. Fixed; caught by
  the axe check.
- CARE-002 and CARE-006 silently relied on running during quiet hours (at night in Addis Ababa). They are now
  independent of the clock, and were checked with quiet hours both covering and not covering the current time.

Remaining follow-up: show automation step failures on the owner dashboard (errors are logged only today).
