# ADR-0018: Configurable automation, follow-up recalls and the staff notice board

Status: Accepted (2026-11-15). Builds on ADR-0016 (Care+).

## Context
The owner asked for automation in the areas where clinics usually lose patients: appointments and follow-up. Staff also
needed a place to communicate with each other. Before this change, appointment messages ran with fixed timings, nothing
tracked "see this patient again in N months", and staff had no shared channel inside the system.

## Decision
1. **Per-facility automation settings** (`care.automation_settings`; defaults apply when no row exists; optimistic
   concurrency via If-Match). These settings cover:
   - confirmation on/off;
   - main reminder (2–72 h before);
   - optional same-day reminder (1–6 h before, always shorter than the main reminder);
   - after-visit follow-up (2–96 h);
   - missed-visit re-booking;
   - quiet hours, which may cross midnight;
   - recall invitation lead (1–60 days) and overdue escalation (1–90 days);
   - automatic check-up recall interval (3–24 months);
   - automatic staff notices.

   A reminder whose quiet-hour shift would land within 15 minutes of the visit is skipped, not sent late.
2. **Recalls** (`care.recalls`) are the "next visit" a clinician or reception sets ("Review in 2 weeks"), plus an
   automatic routine check-up after each attended visit if nothing else is booked. The automation:
   - marks a recall *Booked* when a matching appointment exists (starting no more than 60 days before the due date);
   - marks it *Done* when the patient attends;
   - returns it to *Due* if that appointment is cancelled or missed;
   - sends one invitation before the due date, only with the patient's separate recall consent
     (`consent_recalls`, off by default, recorded in consent history; review round 6 F2);
   - turns an overdue recall into a care task for a call-back.

   Closing a recall by hand requires a reason. Recall notes are staff-only and never go into messages.
3. **Notice board** (schema `comms`). Each notice has:
   - an audience by role (empty means everyone) and a facility, or all facilities;
   - a priority, plus optional pinned, must-acknowledge and expiry settings;
   - per-user read and acknowledgement receipts.

   Who can do what:
   - Anyone with `notice.post` posts Normal or Important notices to their own facility.
   - Urgent, pinned, must-acknowledge and all-branch notices need `notice.manage` (owner and clinic administrator).
   - Authors and managers see receipts, including who has not yet acknowledged.

   The board refuses text that looks like an MRN or an Ethiopian phone number. This is a guard, not a guarantee; the UI
   also warns staff not to write patient details.
4. **Automatic notices** are upserted by a stable key and archived when the condition clears. The key is retired on
   archive, so a returning problem posts a fresh notice. The conditions are:
   - urgent call-backs;
   - recalls to book;
   - yesterday's day close not locked;
   - no backup for 7 days.
5. Each automation step (recalls, notices, planning, dispatch) runs in its own transaction, so a failure in one step
   cannot stop appointment messages.

## Consequences
- RoleCatalog version 7 grants:
  - `notice.post` to front-desk, clinical and finance roles;
  - `notice.manage` to owners and clinic administrators.
- Two defects were found and fixed while testing:
  - a `LATERAL` reference error, and a timestamp cast that broke automatic notices for any clinic that had taken a
    backup. Both had been silently swallowed by step isolation, and both now have regression tests;
  - errors are logged, but are not yet surfaced to the owner (follow-up: show automation health on the dashboard).
- The default recall intervals (6-month check-up, 14-day invitation and escalation) are product defaults, not clinical
  advice. They need clinician review per clinic (HAZ-043).
- Messaging is still sandbox-only until the owner connects Telegram or an SMS gateway.
