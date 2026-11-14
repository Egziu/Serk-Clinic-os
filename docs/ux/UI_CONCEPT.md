# SERK Clinic OS — interface concept

**Principle:** calm, bright, clinical. White surfaces on a soft blue field with a faint dental line-icon backdrop; one
blue accent (clinic-configurable, contrast-checked); content on raised panels so the backdrop never competes with text.

## Structure
| Group | Pages | Who sees it |
|---|---|---|
| Care | Reception, Worklist (encounters), My signature | front desk, nurses, clinicians |
| Money | Billing, Finance, Owner reports | cashiers, finance, owner |
| Insight | Dashboard (with "What needs attention") | management |
| Administration | Clinic setup, Team & roles, Data & backups, Settings, System admin | owner, administrators |
| Me | My account | everyone |

Navigation shows only what the person's permissions allow; the server enforces the same rules on every request. Each
person lands on their main task (clinician → worklist, cashier → billing, owner → dashboard).

## Page anatomy
Title + one-sentence purpose → status/alerts → primary work area (panels) → secondary panels. Tabs (`role=tablist`)
split dense pages (Finance, Team & roles, Data & backups). Destructive or sensitive actions ask for a reason or the
person's password, and say what will be recorded.

## Interaction patterns
- **Jump to (Ctrl/⌘+K):** keyboard command palette, arrow keys + Enter, Esc to close; pages and common actions.
- **Explainable insights:** every card states the numbers and offers "Why am I seeing this?" with the exact rule.
- **Setup checklist:** progress ring + 14 steps with direct links; the letterhead preview updates as you type.
- **Exports:** cards per dataset with date range and format; patient-data cards are marked and need a purpose and
  password; the result shows rows and the file fingerprint.
- **Backups:** passphrase strength meter, explicit acknowledgement that a lost passphrase cannot be recovered, and a
  verify step that proves the file opens and balances.

## Accessibility (WCAG 2.2 AA target)
Visible labels on every input; status by text + symbol, never colour alone; focus order follows reading order; skip
link; `aria-current` in navigation; live regions for results; tables with captions/scopes; no inline styles (CSP);
reduced-motion respected. Automated axe scans run on every E2E screen; manual screen-reader and 200 % zoom checks are
**NOT RUN**.

## Localisation
English, Amharic and Tigrinya architecture; the clinic chooses the default language for printed text and whether dates
show Gregorian, Ethiopian or both (always labelled). New administration screens are English-only for now.
