# ADR-0006 Document rendering: PDFsharp/MigraDoc 6.2.4 (MIT), same renderer for preview and final
- **Status:** Accepted · **Date:** 2026-11-09
- **Decision:** A document freezes a canonical data snapshot (hash) + template code/version at creation. Preview and final are produced by the same pure function `ConsultationSummaryRenderer.Render(snapshot, mode)`; preview adds a watermark and "not valid" footer only. Final PDF bytes are stored write-once with SHA-256 and verified on every download; superseded documents are not served. QR contains an opaque verification URL.
- **Alternatives rejected:** QuestPDF (community license revenue limits), headless Chromium (operational weight on clinic servers), PDFsharp ≥ 6.3 (AGPL-3.0-only).
- **Fonts:** embedded Noto Sans + Noto Sans Ethiopic (OFL-1.1) with per-script run segmentation.
- **Open:** visual (pixel) regression tests; A5 layout review; Amharic template translations require qualified review.
