import { expect, test } from "@playwright/test";
import { bookFirstFreeSlot, expectNoSeriousA11yViolations, login, SHOTS } from "./helpers";

const suffix = Date.now().toString(36).slice(-5);
const given = `Dentpt${suffix}`;

test.describe.configure({ mode: "serial" });

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
  await bookFirstFreeSlot(r, "Dr. Liya Mekonnen (Dentist, synthetic) — Dentistry", 40);
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
  // Headless Chromium has no inline PDF viewer, so the fallback link path is the one exercised here.
  await expect(page.getByRole("status").filter({ hasText: "Recorded Caries on tooth 36" })).toBeVisible();
  await expect(page.getByRole("button", { name: "36 C: lower left first molar, Caries (Mesial, Occlusal)" })).toBeVisible();
  await expect(page.getByRole("table", { name: "Current dental findings" })).toContainText("36 lower left first molar");

  await expectNoSeriousA11yViolations(page, "dental-chart");
  await page.getByRole("heading", { name: "Dental chart (FDI)" }).scrollIntoViewIfNeeded();
  await page.screenshot({ path: `${SHOTS}/05-dental-chart.png` }); // viewport capture: sticky banner shown as the user sees it

  await page.getByLabel(/Primary \(51–85\)/).check();
  await expect(page.getByRole("button", { name: /^55: no finding recorded/ })).toBeVisible();
});

test("reception prints an appointment slip on the default thermal printer", async ({ page }) => {
  await login(page, "reception.a");
  const row = page.getByRole("row").filter({ hasText: given });
  await expect(row).toBeVisible();
  await row.getByRole("button", { name: /Print slip/ }).click();
  await expect(page.getByRole("status").filter({ hasText: new RegExp(`(Print dialog opened for ${given}.*slip|Slip for ${given}.* is ready)`) })).toBeVisible();
  const appts = (await (await page.request.get("/api/v1/appointments")).json()) as { id: string; patientDisplayName: string }[];
  const appt = appts.find((a) => a.patientDisplayName.startsWith(given))!;
  // The print frame holds this patient's slip (printing is triggered by that frame's own load event).
  await expect(page.locator("iframe[title='Appointment slip for printing']")).toHaveAttribute("data-appointment-id", appt.id);
  for (const [paper, widthPt] of [["Thermal58", 164], ["Thermal80", 227], ["A5", 420]] as const) {
    const pdf = await page.request.get(`/api/v1/appointments/${appt.id}/slip?paper=${paper}`);
    expect(pdf.status()).toBe(200);
    const text = (await pdf.body()).toString("latin1");
    const box = /\/MediaBox\s*\[\s*0\s+0\s+([\d.]+)/.exec(text);
    expect(Math.round(Number(box?.[1]))).toBe(widthPt);
  }
});
