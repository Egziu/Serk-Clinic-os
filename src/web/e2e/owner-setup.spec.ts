import { expect, test } from "@playwright/test";
import { readFileSync } from "node:fs";
import { expectNoSeriousA11yViolations, login, PASSWORD, SHOTS } from "./helpers";

const run = Date.now().toString(36).slice(-5);
const PASSPHRASE = `Synthetic backup ${run} Passphrase!`;

test.describe.serial("Owner: setup, roles, exports, backups, insights", () => {
  test("owner jumps to clinic setup with Ctrl+K and saves the profile; the letterhead preview follows", async ({ page }) => {
    await login(page, "owner.a");
    await expect(page.getByRole("heading", { name: "Clinic performance" })).toBeVisible();
    await expect(page.getByRole("region", { name: "What needs attention" })).toBeVisible();

    await page.keyboard.press("Control+k");
    const palette = page.getByRole("dialog", { name: "Jump to" });
    await expect(palette).toBeVisible();
    await page.getByRole("combobox", { name: "Type a page or action" }).fill("motto");
    await expect(palette.getByRole("option", { name: /Clinic setup/ })).toBeVisible();
    await page.keyboard.press("Enter");
    await expect(page.getByRole("heading", { name: "Clinic setup" })).toBeVisible();
    await expect(page.getByRole("region", { name: /Setup checklist/ })).toBeVisible();

    await page.getByLabel(/^Clinic name/).fill(`Bole Smile Dental ${run}`);
    await page.getByLabel(/^Motto/).fill("Gentle care, bright smiles");
    await page.getByLabel("Street / building").fill("Africa Avenue, House 12");
    await page.getByLabel("City", { exact: true }).fill("Addis Ababa");
    await page.getByLabel("Main phone").fill("+251 11 555 0100");
    await page.getByLabel("E-mail").fill("not-an-email");
    await page.getByRole("button", { name: "Save clinic profile" }).click();
    await expect(page.getByRole("alert")).toContainText("e-mail");
    await page.getByLabel("E-mail").fill("info@bole.example");
    await page.getByLabel("Facility licence number").fill("MOH-DEN-0042");
    await page.getByRole("button", { name: "Save clinic profile" }).click();
    await expect(page.getByRole("status").filter({ hasText: /Saved \(version \d+\)/ })).toBeVisible();

    const preview = page.getByRole("region", { name: "Letterhead preview" });
    await expect(preview).toContainText(`Bole Smile Dental ${run}`);
    await expect(preview).toContainText("Gentle care, bright smiles");
    await expect(preview).toContainText("Licence MOH-DEN-0042");
    await expectNoSeriousA11yViolations(page, "clinic-setup");
    await page.screenshot({ path: `${SHOTS}/13-clinic-setup.png`, fullPage: true });
  });

  test("everyone sees roles and responsibilities; the owner also sees the team and permission matrix", async ({ page }) => {
    await login(page, "owner.a");
    await page.getByRole("button", { name: "Team & roles" }).click();
    await expect(page.getByRole("heading", { name: "Team & roles" })).toBeVisible();
    await expect(page.getByRole("article").filter({ hasText: "Owner / management" })).toContainText("Take and verify encrypted backups");
    await page.getByRole("tab", { name: "Permission matrix" }).click();
    await expect(page.getByRole("region", { name: "Permission matrix" }).getByRole("columnheader", { name: "Cashier" })).toBeVisible();
    await page.getByRole("tab", { name: "Team" }).click();
    await expect(page.getByRole("cell", { name: /reception\.a/ })).toBeVisible();
    await expectNoSeriousA11yViolations(page, "team-roles");
    await page.screenshot({ path: `${SHOTS}/14-team-roles.png`, fullPage: true });
  });

  test("owner exports the journal, creates an encrypted backup and verifies it", async ({ page }, info) => {
    await login(page, "owner.a");
    await page.getByRole("button", { name: "Data & backups" }).click();
    await expect(page.getByRole("heading", { name: "Data & backups" })).toBeVisible();

    const journal = page.getByRole("form", { name: "General journal" });
    const [csv] = await Promise.all([page.waitForEvent("download"), journal.getByRole("button", { name: "Download" }).click()]);
    expect(csv.suggestedFilename()).toMatch(/^serk-finance-journal-\d{8}-\d{8}\.csv$/);
    await expect(journal.getByRole("status")).toContainText("rows");

    // Patient data needs a purpose and the password before the button is enabled.
    const register = page.getByRole("form", { name: /Patient register/ });
    await expect(register.getByRole("button", { name: "Download" })).toBeDisabled();

    await page.getByRole("tab", { name: "Backups" }).click();
    await page.getByLabel(/^Backup passphrase/).fill(PASSPHRASE);
    await page.getByLabel("Repeat passphrase").fill(PASSPHRASE);
    await page.getByLabel(/^Your sign-in password/).fill(PASSWORD);
    await page.getByLabel(/I will store the passphrase safely/).check();
    await expect(page.getByLabel(/^Purpose/)).toHaveValue(/backup/);
    const [backup] = await Promise.all([page.waitForEvent("download"), page.getByRole("button", { name: "Create and download backup" }).click()]);
    expect(backup.suggestedFilename()).toMatch(/\.serkbak$/);
    const path = info.outputPath("org.serkbak");
    await backup.saveAs(path);
    expect(readFileSync(path).subarray(0, 8).toString("ascii")).toBe("SERKBAK1");
    await expect(page.getByRole("status").filter({ hasText: "Backup downloaded" })).toBeVisible();

    await page.getByLabel("Backup file (.serkbak)").setInputFiles(path);
    await page.getByLabel("Passphrase", { exact: true }).fill(PASSPHRASE);
    await page.getByRole("button", { name: "Verify", exact: true }).click();
    await expect(page.getByRole("status").filter({ hasText: "Backup is complete and intact." })).toBeVisible();
    await expect(page.getByRole("region", { name: "Backup history" })).toContainText("Verified");
    await expectNoSeriousA11yViolations(page, "data-backups");
    await page.screenshot({ path: `${SHOTS}/15-data-backups.png`, fullPage: true });
  });

  test("a receptionist sees roles but no data exports, backups or team list", async ({ page }) => {
    await login(page, "reception.a");
    await expect(page.getByRole("button", { name: "Data & backups" })).toHaveCount(0);
    await page.getByRole("button", { name: "Team & roles" }).click();
    await expect(page.getByRole("tab", { name: "Team" })).toHaveCount(0);
    await expect(page.getByRole("article").filter({ hasText: "Receptionist" }).first()).toContainText("Register patients");
  });
});
