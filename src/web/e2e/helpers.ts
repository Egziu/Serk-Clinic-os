import { expect, type Page } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";

// Local runs use the published dev password; hosted Demo runs pass their own via SERK_E2E_PASSWORD.
export const PASSWORD = process.env.SERK_E2E_PASSWORD ?? "Synthetic-Demo-2026!";
export const SHOTS = "../../docs/evidence/milestone-A/screenshots";

export async function login(page: Page, username: string): Promise<void> {
  await page.goto("/");
  await page.getByLabel("Username").fill(username);
  await page.getByLabel("Password").fill(PASSWORD);
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page.getByText("Signed in as")).toBeVisible();
}

/** WCAG 2.x A/AA automated checks. Automated scanning finds only a subset of issues; manual review is still required. */
export async function expectNoSeriousA11yViolations(page: Page, context: string): Promise<void> {
  const results = await new AxeBuilder({ page: page as never }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
  const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
  if (process.env.SERK_AXE_DEBUG) for (const v of serious) for (const n of v.nodes.slice(0, 20)) console.error(`[axe] ${v.id} ${n.target.join(" ")} :: ${n.failureSummary?.replace(/\s+/g, " ").slice(0, 220)}`);
  expect(serious.map((v) => `${context}: ${v.id} (${v.nodes.length}) ${v.help}`)).toEqual([]);
}

/** Facility-local (EAT, UTC+3) HH:mm a few minutes ahead, rounded to 5 minutes. */
export function eatTimeSoon(minutesAhead: number): { date: string; time: string } {
  const t = new Date(Date.now() + 3 * 3600_000 + minutesAhead * 60_000);
  t.setUTCMinutes(Math.ceil(t.getUTCMinutes() / 5) * 5, 0, 0);
  const iso = t.toISOString();
  return { date: iso.slice(0, 10), time: iso.slice(11, 16) };
}
