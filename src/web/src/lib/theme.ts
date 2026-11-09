// Theme handling: user preference (light / dark / system) stored per browser, and the clinic accent colour applied at runtime
// via CSSOM custom properties (allowed by CSP; no inline style attributes).

export type ThemePreference = "system" | "light" | "dark";
const KEY = "serk.theme";

export function loadThemePreference(): ThemePreference {
  try {
    const v = localStorage.getItem(KEY);
    return v === "light" || v === "dark" ? v : "system";
  } catch {
    return "system";
  }
}

export function applyThemePreference(pref: ThemePreference): void {
  const root = document.documentElement;
  if (pref === "system") root.removeAttribute("data-theme");
  else root.setAttribute("data-theme", pref);
  try {
    localStorage.setItem(KEY, pref);
  } catch {
    /* storage unavailable (private mode) - preference simply not remembered */
  }
}

function hexToRgb(hex: string): [number, number, number] {
  const n = parseInt(hex.slice(1), 16);
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}

function rgbToHex([r, g, b]: [number, number, number]): string {
  return "#" + [r, g, b].map((c) => Math.round(Math.max(0, Math.min(255, c))).toString(16).padStart(2, "0")).join("");
}

function mix(a: string, b: string, t: number): string {
  const [x, y] = [hexToRgb(a), hexToRgb(b)];
  return rgbToHex([x[0] + (y[0] - x[0]) * t, x[1] + (y[1] - x[1]) * t, x[2] + (y[2] - x[2]) * t]);
}

/** WCAG 2.x relative luminance contrast ratio (mirrors the server check). */
export function contrastRatio(a: string, b: string): number {
  const lum = (hex: string) =>
    hexToRgb(hex)
      .map((c) => c / 255)
      .map((c) => (c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4)))
      .reduce((acc, c, i) => acc + c * [0.2126, 0.7152, 0.0722][i]!, 0);
  const [l1, l2] = [lum(a), lum(b)];
  return (Math.max(l1, l2) + 0.05) / (Math.min(l1, l2) + 0.05);
}

export const isHexColor = (v: string) => /^#[0-9a-fA-F]{6}$/.test(v);

export interface AccentTokens {
  brand: string;
  brandStrong: string;
  brandSoft: string;
  brandDark: string;
  brandSoftDark: string;
}

// Dark-theme surfaces and the dark-mode text colour on accent buttons (keep in sync with styles.css).
const DARK_SURFACES = ["#121614", "#1a1f1c", "#222824"];
const DARK_ON_ACCENT = "#0e1311";
const MIN_TEXT_CONTRAST = 4.5;

/**
 * Derives every accent token so that text drawn in the accent stays >= 4.5:1 in both themes:
 * light accent on its soft tint, dark-mode accent on every dark surface and on its own soft tint,
 * and dark button text on the dark-mode accent. Lightness is increased step by step until all pairs pass;
 * pure white always passes, so the loops terminate.
 */
export function deriveAccent(hex: string): AccentTokens {
  let softT = 0.88;
  let brandSoft = mix(hex, "#ffffff", softT);
  while (contrastRatio(hex, brandSoft) < MIN_TEXT_CONTRAST && softT < 1) {
    softT = Math.min(1, softT + 0.01);
    brandSoft = mix(hex, "#ffffff", softT);
  }
  const brandSoftDark = mix(hex, "#141816", 0.78);
  let darkT = 0.45;
  let brandDark = mix(hex, "#ffffff", darkT);
  const darkOk = (c: string) =>
    [...DARK_SURFACES, brandSoftDark, DARK_ON_ACCENT].every((bg) => contrastRatio(c, bg) >= MIN_TEXT_CONTRAST);
  while (!darkOk(brandDark) && darkT < 1) {
    darkT = Math.min(1, darkT + 0.02);
    brandDark = mix(hex, "#ffffff", darkT);
  }
  return { brand: hex, brandStrong: mix(hex, "#000000", 0.2), brandSoft, brandDark, brandSoftDark };
}

/** Applies the clinic accent tokens at runtime via CSSOM (no inline style attributes, CSP-safe). */
export function applyAccent(hex: string): void {
  if (!isHexColor(hex)) return;
  const t = deriveAccent(hex.toLowerCase());
  const s = document.documentElement.style;
  s.setProperty("--brand", t.brand);
  s.setProperty("--brand-strong", t.brandStrong);
  s.setProperty("--brand-soft", t.brandSoft);
  s.setProperty("--brand-dark", t.brandDark);
  s.setProperty("--brand-soft-dark", t.brandSoftDark);
}
