import { expect, test } from "@playwright/test";
import { eatTimeSoon, expectNoSeriousA11yViolations, login, SHOTS } from "./helpers";

const suffix = Date.now().toString(36).slice(-5);
const given = `Dentpt${suffix}`;

test("dentist charts a surface finding by keyboard with wrong-tooth confirmation", async ({ page, browser }) => {
  // Reception: register and book with the dentist, then check in.
  const rctx = await browser.newContext();
  const r = await rctx.newPage();
  await login(r, "reception.a");
  await r.getByLabel("Given name", { exact: true }).fill(given);
  await r.getByLabel("Father's name", { exact: true }).fill("Synthetic");
  await r.getByLabel("Sex", { exact: true }).selectOption("Male");
  await r.getByLabel("Date of birth (Gregorian)").fill("1985-01-15");
  await r.getByRole("button", { name: "Register patient" }).click();
  await expect(r.getByRole("status").filter({ hasText: "registered as MRN-" })).toBeVisible();
  const slot = eatTimeSoon(40);
  await r.getByLabel("Clinician").selectOption({ label: "Dr. Liya Mekonnen (Dentist, synthetic) — Dentistry" });
  await r.getByLabel("Date (Gregorian)", { exact: true }).fill(slot.date);
  await r.getByLabel("Time (EAT, UTC+3)").fill(slot.time);
  await r.getByRole("button", { name: "Book", exact: true }).click();
  const row = r.getByRole("row").filter({ hasText: given });
  await row.getByRole("button", { name: /Check in/ }).click();
  await r.getByLabel(/I confirmed the patient's name/).check();
  await r.getByRole("dialog").getByRole("button", { name: "Check in" }).click();
  await expect(row.getByText("Checked in")).toBeVisible();
  await rctx.close();

  // Dentist.
  await login(page, "dentist.a");
  await page.getByRole("button", { name: new RegExp(`Open encounter for ${given}`) }).click();
  await expect(page.getByRole("heading", { name: "Dental chart (FDI)" })).toBeVisible();

  const tooth36 = page.getByRole("button", { name: /^36: no finding recorded/ });
  await tooth36.focus();
  await page.keyboard.press("Enter");
  await expect(page.getByRole("heading", { name: "Record finding — tooth 36" })).toBeVisible();
  await expect(page.getByLabel("Incisal")).toHaveCount(0); // posterior tooth: no incisal edge offered
  await page.getByLabel("Condition").selectOption("Caries");
  await page.getByLabel("Occlusal").check();
  await page.getByLabel("Mesial").check();

  await page.getByLabel(/Confirm tooth number/).fill("46"); // wrong tooth typed
  await page.getByRole("button", { name: "Record finding" }).click();
  await expect(page.getByRole("alert")).toContainText("does not match the selected tooth");

  await page.getByLabel(/Confirm tooth number/).fill("36");
  await page.getByRole("button", { name: "Record finding" }).click();
  await expect(page.getByRole("status").filter({ hasText: "Recorded Caries on tooth 36" })).toBeVisible();
  await expect(page.getByRole("button", { name: "36 C: lower left first molar, Caries (Mesial, Occlusal)" })).toBeVisible();
  await expect(page.getByRole("table", { name: "Current dental findings" })).toContainText("36 lower left first molar");

  await expectNoSeriousA11yViolations(page, "dental-chart");
  await page.getByRole("heading", { name: "Dental chart (FDI)" }).scrollIntoViewIfNeeded();
  await page.screenshot({ path: `${SHOTS}/05-dental-chart.png` }); // viewport capture: sticky banner shown as the user sees it

  await page.getByLabel(/Primary \(51–85\)/).check();
  await expect(page.getByRole("button", { name: /^55: no finding recorded/ })).toBeVisible();
});
