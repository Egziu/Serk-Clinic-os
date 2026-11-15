# ADR-0017: Delegated staff and role administration for clinic owners

Date: 2026-11-15 · Status: Accepted (engineering)

Owners asked to add staff and customise roles themselves. New capability `staff.manage` (Owner by default) exposes
`/api/v1/organisation/admin/*`, re-using the super-administration service with every call confined to the caller's
organisation: only people whose every assignment is in this organisation, never the caller, never a super administrator;
facilities, custom roles and the change log of this organisation only; other organisations' assignments are hidden.
Changes go to the same append-only change log with the organisation as tenant.

Consequence: an owner can create a second account and give it clinical access (as a super administrator can) — audited,
not dual-controlled. The two-person rule (ACCESS-014) remains an open owner decision.
