import { expect, test } from "@playwright/test";
import { expectNoSeriousA11yViolations, login, SHOTS } from "./helpers";

// Unique per run so the suite can be re-run against a long-lived deployment.
const run = Date.now().toString(36).slice(-6);
const roleName = `Front desk plus ${run}`;
const username = `e2e.desk.${run}`;
const newPassword = `Own-Strong-Password-${run}!`;

test.describe.serial("System super administration", () => {
  let temporaryPassword = "";

  test("super admin builds a custom role and gives a new person tailored access", async ({ page }) => {
    await login(page, "super.admin");
    await expect(page.getByRole("heading", { name: "System administration" })).toBeVisible();
    await expect(page.getByText("No patient data is shown here.")).toBeVisible();
    await expect(page.getByRole("group", { name: "System overview" })).toContainText("Super administrators");
    // The super admin has no clinical screens.
    await expect(page.getByRole("button", { name: "Reception" })).toHaveCount(0);
    await expectNoSeriousA11yViolations(page, "admin-overview");

    // Roles: clone the built-in Receptionist, rename, add analytics.
    await page.getByRole("tab", { name: "Roles" }).click();
    await page.getByLabel("Organisation").selectOption({ label: "Abay Family Clinic (SYNTHETIC)" });
    await page.getByRole("button", { name: "Clone Receptionist" }).click();
    const editor = page.getByRole("form", { name: "New custom role" });
    await editor.getByLabel("Role name").fill(roleName);
    await editor.getByRole("checkbox", { name: /View analytics/ }).check();
    await editor.getByRole("button", { name: "Create role" }).click();
    await expect(page.getByRole("table", { name: "Roles" })).toContainText(roleName);
    await expectNoSeriousA11yViolations(page, "admin-roles");

    // People: create a person (temporary password generated in the browser).
    await page.getByRole("tab", { name: "People & access" }).click();
    await page.getByRole("button", { name: "+ New person" }).click();
    const create = page.getByRole("form", { name: "New person" });
    await create.getByLabel("Full name").fill(`E2E Desk ${run} (synthetic)`);
    await create.getByLabel("Username").fill(username);
    temporaryPassword = await create.getByLabel("Temporary password").inputValue();
    expect(temporaryPassword.length).toBeGreaterThanOrEqual(12);
    await create.getByRole("button", { name: "Create person" }).click();
    await expect(page.getByRole("heading", { name: `E2E Desk ${run} (synthetic)` })).toBeVisible();
    await expect(page.getByText("Password change pending").first()).toBeVisible();

    // Access: custom role at Bole, but remove "Book appointments"; preview reflects it live.
    await page.getByRole("button", { name: "+ Give access to a facility" }).click();
    const access = page.getByRole("form", { name: "New assignment" });
    await access.getByLabel("Facility").selectOption({ label: "Abay Family Clinic - Bole Branch — Abay Family Clinic (SYNTHETIC)" });
    await access.getByRole("checkbox", { name: new RegExp(roleName) }).check();
    await expect(access.getByRole("list", { name: "Effective permissions" })).toContainText("Book appointments");
    await access.getByText(/Fine-tune individual permissions/).click();
    await access.getByLabel("Override for Book appointments").selectOption("revoke");
    await expect(access.getByRole("list", { name: "Effective permissions" })).not.toContainText("Book appointments");
    await expect(access.getByRole("list", { name: "Effective permissions" })).toContainText("View analytics");
    await access.getByLabel("Reason for the change").fill("E2E onboarding (synthetic)");
    await page.screenshot({ path: `${SHOTS}/09-admin-access-editor.png`, fullPage: true });
    await access.getByRole("button", { name: "Give access" }).click();
    await expect(page.getByRole("status").filter({ hasText: "Access saved" })).toBeVisible();
    await expect(page.getByRole("table", { name: /^Access of/ })).toContainText("− appointment.book");
    await expect(page.getByRole("table", { name: "Change history" })).toContainText("Access given");
    await expectNoSeriousA11yViolations(page, "admin-person");

    // Super admin cannot edit its own access.
    await page.getByLabel("Search people").fill("super.admin");
    await page.getByRole("button", { name: /Open Saba Kebede/ }).click();
    await expect(page.getByText("This is your own account.")).toBeVisible();
    await expect(page.getByRole("button", { name: "+ Give access to a facility" })).toHaveCount(0);
  });

  test("the new person must change the temporary password, then sees exactly the granted screens", async ({ page }) => {
    await page.goto("/");
    await page.getByLabel("Username").fill(username);
    await page.getByLabel("Password").fill(temporaryPassword);
    await page.getByRole("button", { name: "Sign in" }).click();
    await expect(page.getByRole("heading", { name: "Choose your own password" })).toBeVisible();
    await expect(page.getByRole("button", { name: "Reception" })).toHaveCount(0); // nothing else available
    await expectNoSeriousA11yViolations(page, "forced-password-change");
    await page.getByLabel("Temporary password").fill(temporaryPassword);
    await page.getByLabel(/^New password/).fill(newPassword);
    await page.getByLabel("Repeat new password").fill(newPassword);
    await page.getByRole("button", { name: "Change password" }).click();

    await expect(page.getByText("Signed in as")).toBeVisible();
    await expect(page.getByRole("button", { name: "Reception" })).toBeVisible();
    await expect(page.getByRole("button", { name: "Dashboard" })).toBeVisible(); // analytics added by the custom role
    await expect(page.getByRole("button", { name: "System admin" })).toHaveCount(0);
    await page.getByRole("button", { name: "Reception" }).click();
    // Booking was removed for this person: the API refuses it even though the role includes it.
    const csrf = ((await (await page.request.get("/api/v1/auth/session")).json()) as { csrfToken: string }).csrfToken;
    const response = await page.request.post("/api/v1/appointments", { data: {}, headers: { "X-CSRF-Token": csrf, "Idempotency-Key": crypto.randomUUID() } });
    expect(response.status()).toBe(403);
    expect(((await response.json()) as { code: string }).code).toBe("capability_required");
    // ...while a permission the role does grant still works.
    expect((await page.request.get("/api/v1/patients?query=Selam")).status()).toBe(200);
  });

  test("clinic administrators cannot open system administration", async ({ page }) => {
    await login(page, "admin.a");
    await expect(page.getByRole("button", { name: "System admin" })).toHaveCount(0);
    await page.goto("/admin");
    await expect(page.getByRole("heading", { name: "System administration" })).toHaveCount(0);
    const api = await page.request.get("/api/v1/platform/overview");
    expect(api.status()).toBe(403);
  });
});
