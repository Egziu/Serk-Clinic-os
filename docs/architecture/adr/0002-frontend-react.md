# ADR-0002 Frontend: React + TypeScript (Vite), single stack
- **Status:** Accepted · **Date:** 2026-11-09
- **Decision:** React 19 + TypeScript 5.9 (strict) built by Vite and served by the API host (same origin). No Blazor. No UI framework dependency; a small in-repo design system (`src/web/src/styles.css`).
- **Why:** Mandate preference, ecosystem for accessible components and Playwright/axe testing; TypeScript 7 (native) postponed until typescript-eslint supports it.
- **Consequences:** Same-origin enables `SameSite=Strict` cookies and a strict CSP (`script-src 'self'`, no inline styles — enforced by ESLint).
