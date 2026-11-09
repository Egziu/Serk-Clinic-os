import { expect, test } from "@playwright/test";
import { expectNoSeriousA11yViolations, login, PASSWORD, SHOTS } from "./helpers";

test.describe.serial("Management: branding, colour, printers, analytics", () => {
  test("clinic admin customises colour, uploads logo and stamp, and classifies printers", async ({ page }) => {
    await login(page, "admin.a");
    await page.getByRole("button", { name: "Settings" }).click();
    await expect(page.getByRole("heading", { name: "Clinic settings" })).toBeVisible();

    // Too-light colour is blocked client-side (and server-side, see integration tests).
    await page.getByLabel("Accent colour hex code").fill("#ffcc00");
    await expect(page.getByText(/too light/)).toBeVisible();
    await expect(page.getByRole("button", { name: "Save colour" })).toBeDisabled();

    // Colour picker.
    await page.getByLabel("Accent colour", { exact: true }).fill("#0f5132");
    await expect(page.getByLabel("Accent colour hex code")).toHaveValue("#0f5132");
    await expect(page.getByText(/readable \(WCAG AA\)/)).toBeVisible();
    await page.getByRole("button", { name: "Save colour" }).click();
    await expect(page.getByText("Accent colour saved")).toBeVisible();
    expect(await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue("--brand").trim())).toBe("#0f5132");

    await page.getByLabel("Choose clinic logo image").setInputFiles("e2e/fixtures/logo.png");
    await page.getByRole("button", { name: "Upload clinic logo" }).click();
    await expect(page.getByRole("img", { name: "Current clinic logo" })).toBeVisible();
    await page.getByLabel("Choose official stamp image").setInputFiles("e2e/fixtures/stamp.png");
    await page.getByRole("button", { name: "Upload official stamp" }).click();
    await expect(page.getByRole("img", { name: "Current official stamp" })).toBeVisible();

    // A non-image renamed .png is rejected with a clear message.
    await page.getByLabel("Choose clinic logo image").setInputFiles({ name: "evil.png", mimeType: "image/png", buffer: Buffer.from("<script>alert(1)</script>") });
    await page.getByRole("button", { name: "Upload clinic logo" }).click();
    await expect(page.getByRole("alert")).toContainText("Only PNG or JPEG images are accepted");

    // Printers: thermal cannot be used for clinical documents.
    const add = page.getByRole("form", { name: "Add printer" });
    await add.getByLabel("Name").fill("Reception thermal");
    await add.getByLabel("Paper").selectOption("Thermal80");
    await expect(add.getByRole("option", { name: /Clinical documents \(not on thermal\)/ })).toBeDisabled();
    await add.getByRole("button", { name: "Add printer" }).click();
    await expect(page.getByRole("table", { name: "Printer profiles" })).toContainText("Reception thermal");
    await add.getByLabel("Name").fill("Office laser A3");
    await add.getByLabel("Paper").selectOption("A3");
    await add.getByLabel("Used for").selectOption("ClinicalDocuments");
    await add.getByRole("button", { name: "Add printer" }).click();
    const table = page.getByRole("table", { name: "Printer profiles" });
    await expect(table).toContainText("Reception thermal");
    await expect(table).toContainText("Thermal roll 80 mm");
    await expect(table).toContainText("A3 (297 × 420 mm)");
    await expect(page.locator(".shell-nav .brand img")).toBeVisible(); // logo in sidebar

    await expectNoSeriousA11yViolations(page, "settings");
    await page.screenshot({ path: `${SHOTS}/06-settings-branding.png`, fullPage: true });
  });

  test("owner dashboard renders statistics and charts, in light and dark themes", async ({ page }) => {
    await login(page, "owner.a");
    await expect(page.getByRole("heading", { name: "Clinic performance" })).toBeVisible();
    await expect(page.getByText("Appointments", { exact: true }).first()).toBeVisible();
    await expect(page.getByText(/95% CI|n = 0/).first()).toBeVisible();
    await expect(page.getByRole("img", { name: /Daily appointments and 7-day moving average/ })).toBeVisible();
    await expect(page.getByRole("img", { name: /Arrivals by weekday and hour/ })).toBeVisible();
    await expect(page.getByText(/Computed in [\d.]+ ms on the server/)).toBeVisible();
    await page.getByRole("img", { name: /Daily appointments/ }).hover(); // hover tooltip
    await page.screenshot({ path: `${SHOTS}/07-dashboard-light.png`, fullPage: true });
    // Keyboard reading of the line chart is announced through a live region.
    await page.getByRole("img", { name: /Daily appointments/ }).focus();
    await page.keyboard.press("End");
    await expect(page.locator("p[aria-live='polite']").first()).toContainText(/Appointments \d+, 7-day average/);
    const tableToggle = page.getByRole("button", { name: "Table view" }).first();
    await tableToggle.click();
    await expect(tableToggle).toHaveAttribute("aria-pressed", "true");
    await expect(page.getByRole("columnheader", { name: "7-day avg" })).toBeVisible();
    await tableToggle.click();
    // Owners cannot reach patient-level screens.
    await expect(page.getByRole("button", { name: "Reception" })).toHaveCount(0);
    await expectNoSeriousA11yViolations(page, "dashboard-light");

    await page.getByRole("button", { name: "Dark theme" }).click();
    await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
    await expectNoSeriousA11yViolations(page, "dashboard-dark");
    await page.screenshot({ path: `${SHOTS}/08-dashboard-dark.png`, fullPage: true });
    await page.reload();
    await expect(page.locator("html")).toHaveAttribute("data-theme", "dark"); // preference remembered
    await page.getByRole("button", { name: "Light theme" }).click();

    const csv = await page.request.get("/api/v1/analytics/daily.csv");
    expect(csv.status()).toBe(200);
    expect((await csv.text()).split("\n")[0]).toBe("date_gregorian,date_ethiopian,appointments,attended,no_show,cancelled,moving_average_7d");
  });

  test("clinician uploads own signature", async ({ page }) => {
    await page.goto("/");
    await page.getByLabel("Username").fill("dr.hana");
    await page.getByLabel("Password").fill(PASSWORD);
    await page.getByRole("button", { name: "Sign in" }).click();
    await page.getByRole("button", { name: /Abay Family Clinic - Bole Branch/ }).click(); // two facilities: choose one
    await expect(page.getByText("Signed in as")).toBeVisible();
    await page.getByRole("button", { name: "My signature" }).click();
    await page.getByLabel("Choose signature image image").setInputFiles("e2e/fixtures/signature.jpg");
    await page.getByRole("button", { name: "Upload signature image" }).click();
    await expect(page.getByRole("img", { name: "Current signature image" })).toBeVisible();
    await expectNoSeriousA11yViolations(page, "signature");
  });
});
