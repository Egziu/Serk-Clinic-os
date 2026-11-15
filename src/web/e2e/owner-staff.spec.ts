import { expect, test } from "@playwright/test";
import { expectNoSeriousA11yViolations, login, SHOTS } from "./helpers";

const run = Date.now().toString(36).slice(-5);

test("owner creates a custom role and adds a new staff member with it; access can be fine-tuned", async ({ page }) => {
  await login(page, "owner.a");
  await page.getByRole("button", { name: "Team & roles" }).click();
  await expect(page.getByRole("tab", { name: "Manage people" })).toHaveAttribute("aria-selected", "true");

  // Custom role: clone the built-in Receptionist and add "Work the care desk".
  await page.getByRole("tab", { name: "Custom roles" }).click();
  await page.getByRole("button", { name: "Clone Receptionist" }).click();
  const editor = page.getByRole("form", { name: "New custom role" });
  await editor.getByLabel("Role name").fill(`Front desk & care ${run}`);
  await editor.getByRole("checkbox", { name: /Work the care desk/ }).check();
  await editor.getByRole("button", { name: "Create role" }).click();
  await expect(page.getByRole("table", { name: "Roles" })).toContainText(`Front desk & care ${run}`);

  // New person with that role at Bole.
  await page.getByRole("tab", { name: "Manage people" }).click();
  await page.getByRole("button", { name: "+ New person" }).click();
  const create = page.getByRole("form", { name: "New person" });
  await create.getByLabel("Full name").fill(`Synthetic Front Desk ${run}`);
  await create.getByLabel("Username").fill(`desk.${run}`);
  await create.getByRole("checkbox", { name: new RegExp(`Front desk & care ${run}`) }).check();
  await create.getByRole("button", { name: "Create person" }).click();
  await expect(page.getByRole("heading", { name: `Synthetic Front Desk ${run}` })).toBeVisible();
  await expect(page.getByText("Password change pending").first()).toBeVisible();
  await expect(page.getByRole("table", { name: /^Access of/ })).toContainText(`Front desk & care ${run}`);
  await expectNoSeriousA11yViolations(page, "owner-staff");
  await page.screenshot({ path: `${SHOTS}/19-owner-staff.png`, fullPage: true });

  // The owner cannot change their own access.
  await page.getByLabel("Search people").fill("owner.a");
  await page.getByRole("button", { name: /^Open .*Owner/ }).click();
  await expect(page.getByText(/your own|cannot change/i).first()).toBeVisible();
});
