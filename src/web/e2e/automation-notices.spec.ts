import { expect, test } from "@playwright/test";
import { bookFirstFreeSlot, expectNoSeriousA11yViolations, login, SHOTS } from "./helpers";

const run = Date.now().toString(36).slice(-5);
const given = `Recallpt${run}`;

test.describe.serial("Automation, follow-up recalls and the notice board", () => {
  test("owner tunes appointment automation", async ({ page }) => {
    await login(page, "owner.a");
    await page.getByRole("button", { name: "Care+" }).click();
    await page.getByRole("tab", { name: "Automation" }).click();
    const form = page.getByRole("form", { name: "Automation settings" });
    await form.getByLabel("Second reminder on the day").check();
    await form.getByRole("button", { name: "Save automation" }).click();
    await expect(form.getByRole("status").filter({ hasText: "Saved" })).toBeVisible();
    await expectNoSeriousA11yViolations(page, "automation");
    await page.screenshot({ path: `${SHOTS}/20-automation-settings.png`, fullPage: true });
  });

  test("dentist schedules a follow-up from the encounter and reception sees it in Recalls", async ({ page, browser }) => {
    const rctx = await browser.newContext();
    const r = await rctx.newPage();
    await login(r, "reception.a");
    await r.getByLabel("Given name", { exact: true }).fill(given);
    await r.getByLabel("Father's name", { exact: true }).fill("Synthetic");
    await r.getByLabel("Sex", { exact: true }).selectOption("Female");
    await r.getByLabel("Date of birth (Gregorian)").fill("1990-03-10");
    await r.getByRole("button", { name: "Register patient" }).click();
    await expect(r.getByRole("status").filter({ hasText: "registered as MRN-" })).toBeVisible();
    await bookFirstFreeSlot(r, "Dr. Liya Mekonnen (Dentist, synthetic) — Dentistry", 50);
    const row = r.getByRole("row").filter({ hasText: given });
    await row.getByRole("button", { name: /Check in/ }).click();
    await r.getByLabel(/I confirmed the patient's name/).check();
    await r.getByRole("dialog").getByRole("button", { name: "Check in" }).click();
    await expect(row.getByText("Checked in")).toBeVisible();

    await login(page, "dentist.a");
    await page.getByRole("button", { name: new RegExp(`Open encounter for ${given}`) }).click();
    const panel = page.getByRole("form", { name: new RegExp(`Next visit for ${given}`) });
    await panel.getByLabel("Purpose").selectOption("Review");
    await panel.getByLabel("See again in").selectOption("2 weeks");
    await panel.getByLabel(/Note for the team/).fill("Check healing after filling");
    await panel.getByRole("button", { name: "Schedule follow-up" }).click();
    await expect(panel.getByRole("status")).toContainText("Follow-up scheduled");
    await expect(panel).toContainText("Waiting to book");

    await r.getByRole("button", { name: "Care+" }).click();
    await r.getByRole("tab", { name: "Recalls" }).click();
    await r.getByLabel("Show").selectOption("all");
    await expect(r.getByRole("row").filter({ hasText: given })).toContainText("Check healing after filling");
    await expectNoSeriousA11yViolations(r, "recalls");
    await r.screenshot({ path: `${SHOTS}/21-recalls.png`, fullPage: true });
    await rctx.close();
  });

  test("owner posts a must-read notice; reception acknowledges; owner sees who has read it", async ({ page, browser }) => {
    const title = `Sterilisation protocol ${run}`;
    await login(page, "owner.a");
    await page.getByRole("button", { name: /^Notice board/ }).first().click();
    await page.getByRole("button", { name: "New notice" }).click();
    const form = page.getByRole("form", { name: "New notice" });
    await form.getByLabel("Title").fill(title);
    await form.getByLabel(/^Message/).fill("Patient MRN-000123 needs a callback");
    await form.getByLabel("Priority").selectOption("Important");
    await form.getByLabel("Reception").check();
    await form.getByLabel("Everyone must confirm they read it").check();
    await form.getByRole("button", { name: "Post notice" }).click();
    await expect(form.getByRole("alert")).toContainText(/patient/i); // the board refuses patient identifiers
    await form.getByLabel(/^Message/).fill("From Monday every instrument tray is logged in the autoclave book before use.");
    await form.getByRole("button", { name: "Post notice" }).click();
    await expect(page.getByRole("article", { name: title })).toBeVisible();

    const rctx = await browser.newContext();
    const r = await rctx.newPage();
    await login(r, "reception.a");
    await expect(r.getByRole("button", { name: /Notice board, \d+ new/ })).toBeVisible();
    await r.getByRole("button", { name: /Notice board, \d+ new/ }).click();
    const card = r.getByRole("article", { name: title });
    await card.getByRole("button", { name: "I have read and understood" }).click();
    await expect(card).toContainText("You acknowledged");
    await expectNoSeriousA11yViolations(r, "notices");
    await r.screenshot({ path: `${SHOTS}/22-notice-board.png`, fullPage: true });
    await rctx.close();

    await page.reload();
    const own = page.getByRole("article", { name: title });
    await own.getByRole("button", { name: "Who has read it" }).click();
    await expect(own).toContainText(/\d+ of \d+ have opened it/);
  });
});
