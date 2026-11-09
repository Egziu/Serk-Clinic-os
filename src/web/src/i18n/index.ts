import { en, type MessageKey } from "./en";

/**
 * Localization scaffold. English is the reference. Amharic (am) and Tigrinya (ti) catalogues are intentionally EMPTY until
 * produced and reviewed by qualified translators (MASTER_BUILD_PROMPT §2, §6): machine translation is not used for clinical
 * wording. Missing keys fall back to English so the UI never shows blank labels.
 */
export type Locale = "en" | "am" | "ti";

const catalogues: Record<Locale, Partial<Record<MessageKey, string>>> = {
  en,
  am: {},
  ti: {},
};

export const LOCALE_LABELS: Record<Locale, string> = { en: "English", am: "አማርኛ (translation pending review)", ti: "ትግርኛ (translation pending review)" };

let current: Locale = "en";

export function setLocale(locale: Locale): void {
  current = locale;
  document.documentElement.lang = locale;
}

export function t(key: MessageKey): string {
  return catalogues[current][key] ?? en[key];
}
