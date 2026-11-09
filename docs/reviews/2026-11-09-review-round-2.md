# Review round 2 — branding, printing, automation, analytics, UI (2026-11-09)

Two independent read-only AI reviews (security; UX/accessibility) of the new features. AI review is **not** an
independent human security, clinical or legal review.

## Security review — disposition
| ID | Severity | Finding | Disposition | Evidence |
|---|---|---|---|---|
| M1 | Medium | Upload check structural only; metadata kept; JPEG comments could carry HTML; no pixel bound | **Fixed**: PNG pixel data inflated with hard bound and exact-size check, IDAT required, unknown critical chunks rejected; full JPEG walk; ≤ 4 MP; canonical metadata-free bytes stored | BRAND-007 unit + integration |
| M2 | Medium | Asset content cached `private, max-age=300` | **Fixed**: `no-store` | BRAND-001, BRAND-004 |
| M3 | Medium | Signature visible to branding managers; later signature applied to earlier notes | **Fixed**: owner-only; signature in effect at signing time. **Open:** legal decision on signature images in patient copies | BRAND-004, BRAND-005 (mutation-checked) |
| L1 | Low | Small demographic cells re-identifiable | **Fixed** for sex/age (suppressed < 5). Heatmap not suppressed (no demographics) | ANALYTICS-002 |
| L2 | Low | Extreme dates/numeric enums → 500; no rate limit on analytics/uploads | **Fixed** dates + enums. **Open:** per-user rate limits | HARDEN-001 |
| L3 | Low | Server-Timing on login/verify | **Fixed**: omitted there; 0.1 ms resolution | HARDEN-001 |
| L4 | Low | Concurrent uploads 500; orphan objects | **Fixed** race (advisory lock). **Open:** orphan object GC | BRAND-006 |
| L5 | Low | Sweeper grace unbounded; one tenant failure stops all | **Fixed** (≥ 30 min at startup and per sweep; per-tenant isolation). **Open:** domain event for automatic no-show | build + tests |
| L6 | Low | Duplicate auto drafts on concurrent delivery | **Fixed**: per-note-version advisory lock | AUTO-001 |
| Info | — | Regex `$` accepts trailing newline; missing tenant predicate | **Fixed** (`\z`; explicit tenant filter) | THEME-001 |

## UX / accessibility review — disposition
| # | Severity | Finding | Disposition |
|---|---|---|---|
| 1 | High | Slip could print previous patient (timer race) | **Fixed**: print on the frame's own load event, keyed by URL; message names patient; fallback link when the browser has no inline PDF viewer |
| 2 | High | One-click Finalize from list without preview | **Fixed**: Finalize only beside visible preview, confirmation names patient + reference, disabled while in flight |
| 3 | High | Dark-mode accent could fail contrast | **Fixed**: `deriveAccent` lightens until ≥ 4.5:1 on all dark surfaces, soft tint and button text (Vitest over 8 accents) |
| 4 | Medium | Charts mouse-only / silent to screen readers | **Partly fixed**: line chart arrow/Home/End with live-region readout (E2E); bars/cells have `<title>`; table view toggle is a fixed-label `aria-pressed` button. Bars/cells not individually focusable (table view covers) |
| 5 | Medium | Dates ambiguous | **Fixed**: visible "From/To (Gregorian)" labels; period repeated in ISO and E.C. Native date input display still follows browser locale |
| 6 | Medium | Heatmap colour-only, inverted in dark mode | **Fixed**: dark ramp, legend, per-cell titles. Lightest light-mode step remains < 3:1 against white (values in table/titles) |
| 7 | Medium | Theme switch focus ring clipped | **Fixed** (inset outline) |
| 8 | Medium | Sticky banner may cover focus at zoom | **Fixed** (`scroll-padding-top`; banner not sticky below 520 px height). 200% zoom **NOT VERIFIED** by screenshot |
| 9 | Medium | Final PDF preview blank in screenshot | **Open** (headless Chromium has no PDF viewer); PDF/hash equality is asserted by integration tests, not visually |
| 10 | Low | Repeated button names, KPI group, ▲▼ symbols, paper label | **Fixed**; truncated clinician names now have full-text titles. Field-level error association for uploads **open** |

Not run: manual screen-reader pass, 200% zoom/reflow screenshots, real thermal/laser printer output.
