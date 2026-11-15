import { expect, test } from "@playwright/test";
import { expectNoSeriousA11yViolations, login, SHOTS } from "./helpers";

const run = Date.now().toString(36).slice(-5);

test.describe.serial("Care+ and social media", () => {
  test("reception records SMS consent and creates a Telegram link code", async ({ page }) => {
    await login(page, "reception.a");
    await page.getByLabel("Name, MRN, phone or national ID").fill("Selam");
    await page.getByRole("button", { name: "Search" }).click();
    await page.getByRole("button", { name: /^Select Selam/ }).first().click();
    const panel = page.getByRole("form", { name: /Care\+ messages for/ });
    await expect(panel).toBeVisible();
    await panel.getByLabel(/Mobile for SMS/).fill("0911 22 33 44");
    await panel.getByLabel("Send by").selectOption("Sms");
    await panel.getByLabel(/Appointment confirmations, reminders/).check();
    await panel.getByLabel("How did the patient agree?").selectOption("Written");
    await panel.getByRole("button", { name: "Save consent" }).click();
    await expect(panel.getByRole("status").filter({ hasText: "Saved" })).toBeVisible();
    await panel.getByRole("button", { name: /Link Telegram/ }).click();
    await expect(panel.getByText(/\/start [A-Z2-9]{8}/)).toBeVisible();
    await expectNoSeriousA11yViolations(page, "reception-consent");
    await page.screenshot({ path: `${SHOTS}/16-reception-care-consent.png`, fullPage: true });
  });

  test("a clinician writes a health tip, another clinician approves it, the owner schedules a campaign", async ({ page, browser }) => {
    await login(page, "dr.abebe");
    await page.getByRole("button", { name: "Care+" }).click();
    await page.getByRole("tab", { name: "Health tips" }).click();
    await page.getByLabel("Title").fill(`Brush twice daily ${run}`);
    await page.getByLabel(/^Text/).fill("Brush for two minutes with fluoride toothpaste every morning and night, and replace your brush every three months.");
    await page.getByRole("button", { name: "Save draft for approval" }).click();
    const card = page.getByRole("article", { name: `Brush twice daily ${run}` });
    await expect(card).toContainText("Awaiting clinician approval");

    const dctx = await browser.newContext();
    const d = await dctx.newPage();
    await login(d, "dentist.a");
    await d.getByRole("button", { name: "Care+" }).click();
    await d.getByRole("tab", { name: "Health tips" }).click();
    await d.getByRole("article", { name: `Brush twice daily ${run}` }).getByRole("button", { name: "Approve as clinically appropriate" }).click();
    await expect(d.getByRole("article", { name: `Brush twice daily ${run}` })).toContainText("✓ Approved");
    await dctx.close();

    const octx = await browser.newContext();
    const o = await octx.newPage();
    await login(o, "owner.a");
    await o.getByRole("button", { name: "Care+" }).click();
    await o.getByRole("tab", { name: "Campaigns" }).click();
    await o.getByLabel("Approved tip").selectOption({ label: `Brush twice daily ${run} (English)` });
    await o.getByRole("button", { name: "Schedule campaign" }).click();
    await expect(o.getByRole("region", { name: "Campaigns" })).toContainText(`Brush twice daily ${run}`);
    await o.getByRole("tab", { name: "Message templates" }).click();
    await expect(o.getByRole("form", { name: /Reminder before the visit · Amharic/ })).toBeVisible();
    await o.getByRole("tab", { name: "Overview" }).click();
    await expect(o.getByRole("group", { name: "Care+ indicators" })).toContainText("Agreed to reminders");
    await expectNoSeriousA11yViolations(o, "care");
    await o.screenshot({ path: `${SHOTS}/17-care.png`, fullPage: true });
    await octx.close();
  });

  test("owner plans consent-safe social posts and records results", async ({ page }) => {
    await login(page, "owner.a");
    await page.getByRole("button", { name: "Social media" }).click();
    await page.getByRole("tab", { name: "Content planner" }).click();
    await page.getByLabel("Platform").selectOption("Instagram");
    await page.getByLabel("Title / idea").fill(`Smile makeover ${run}`);
    await page.getByLabel(/Shows a patient/).check();
    await page.getByRole("button", { name: "Add to plan" }).click();
    await expect(page.getByRole("alert")).toContainText("consent");
    await page.getByLabel(/Signed media-consent reference/).fill("MC-2026-014");
    await page.getByLabel("Status").selectOption("Scheduled");
    await page.getByRole("button", { name: "Add to plan" }).click();
    await expect(page.getByText(`Smile makeover ${run}`)).toBeVisible();

    await page.getByRole("tab", { name: "Results" }).click();
    await page.getByLabel("Platform").selectOption("Instagram");
    await page.getByLabel("Followers").fill("1250");
    await page.getByLabel(/^Reach/).fill("4000");
    await page.getByLabel(/^Engagements/).fill("200");
    await page.getByLabel(/^Bookings/).fill("3");
    await page.getByRole("button", { name: "Save results" }).click();
    await expect(page.getByRole("cell", { name: "5.0%" }).first()).toBeVisible();
    await page.getByRole("tab", { name: "Overview" }).click();
    await expect(page.getByRole("article", { name: "Instagram" })).toContainText("1,250 followers");
    await expectNoSeriousA11yViolations(page, "social");
    await page.screenshot({ path: `${SHOTS}/18-social.png`, fullPage: true });
  });
});
