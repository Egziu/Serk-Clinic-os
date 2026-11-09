# ADR-0012 Branding, printing classes, automation and management analytics
- **Status:** Accepted (engineering); clinical/legal content PENDING REVIEW · **Date:** 2026-11-09
- **Context:** Clinic owners asked for custom colour, logo/stamp/signature uploads, thermal vs standard printers in A3/A4/A5,
  automation, and detailed management statistics, without weakening record integrity or privacy.
- **Decisions:**
  1. New **Configuration** module owns branding and printers; other modules read it only through `IBrandingReader`.
  2. Uploaded images are validated and **canonicalised** (metadata-free re-serialisation) in-process without a new image
     library; only the canonical bytes are stored, hashed and embedded. Full decode/re-encode was not adopted to avoid
     a new native dependency; revisit if a vetted MIT decoder is approved.
  3. **Paper classes:** Standard (A3/A4/A5) for clinical documents; Thermal (58/80 mm) only for appointment slips —
     enforced by a DB CHECK, not just the UI.
  4. **Branding is frozen** into document snapshots by asset id + SHA-256; signature image = the one in effect at
     signing time; signature and stamp appear on finals only.
  5. **Automation** never finalizes or signs: it prepares watermarked drafts (outbox, idempotent) and marks long-past
     bookings as no-show (audited, bounded grace).
  6. **Analytics** run on-request SQL over PHI-free `security_invoker` views (no warehouse, no extra copy of PHI);
     Wilson 95% CIs, OLS trend, percentiles; small cells (<5) suppressed; results labelled operational, not clinical.
  7. Charts are hand-written SVG (no chart dependency), with keyboard/live-region readout and a table alternative.
- **Consequences:** Analytics latency grows with data volume (measured: docs/performance/PERF_REPORT.md); a materialised
  daily rollup is the next step if 12-month queries exceed targets on clinic hardware.
- **Evidence:** RELEASE_CRITICAL_SCENARIOS BRAND-001..007, THEME-001, PRINT-001/002, DOC-BRAND-001, DOC-FIN-001,
  AUTO-001/002, ANALYTICS-001/002, HARDEN-001.
