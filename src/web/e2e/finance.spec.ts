import { expect, test } from "@playwright/test";
import { expectNoSeriousA11yViolations, login, SHOTS } from "./helpers";

const run = Date.now().toString(36).slice(-6);
const reference = `TB-E2E-${run}`;
// The day-close part locks today's business day, so it only runs against a disposable database (not a shared live demo).
const disposable = !process.env.SERK_E2E_BASE_URL;

test.describe.serial("Finance: billing → payment → drawer → day close → owner report", () => {
  test("cashier opens the drawer, bills from the price list, takes cash and records a mobile payment attempt", async ({ page }) => {
    await login(page, "cashier.a");
    await expect(page.getByRole("heading", { name: "Billing" })).toBeVisible();
    const drawer = page.getByRole("region", { name: /My cash drawer/ });
    const openButton = drawer.getByRole("button", { name: "Open drawer" });
    await expect(openButton.or(drawer.getByText("Drawer open"))).toBeVisible(); // wait for the panel to load
    if (await openButton.isVisible()) {
      await drawer.getByLabel("Opening float (ETB)").fill("1000");
      await drawer.getByRole("button", { name: "Open drawer" }).click();
    }
    await expect(drawer.getByText("Drawer open")).toBeVisible();
    // Blind count: the expected cash is not shown while the drawer is open.
    await expect(drawer.getByText(/Expected/)).toHaveCount(0);

    await page.getByLabel("Name, MRN or phone").fill("Selam");
    await page.getByRole("button", { name: "Search" }).click();
    await page.getByRole("button", { name: /^Bill Selam/ }).click();

    // Cash invoice: dental examination 400.00
    await page.getByLabel("Service", { exact: true }).selectOption({ label: "Dental examination — 400.00 ETB" });
    await page.getByRole("button", { name: "Add service" }).click();
    await page.getByRole("button", { name: /Prepare invoice \(400\.00 ETB\)/ }).click();
    await expect(page.getByRole("status").filter({ hasText: "Invoice prepared." })).toBeVisible();
    const draft = page.getByRole("article", { name: "Invoice draft" }).first();
    await draft.getByRole("button", { name: "Issue invoice" }).click();
    await expect(page.getByRole("status").filter({ hasText: "Invoice issued." })).toBeVisible();
    const cashInvoice = page.getByRole("article").filter({ hasText: "Balance due" }).filter({ hasText: "400.00 ETB" }).first();
    await cashInvoice.getByRole("button", { name: "Take cash" }).click();
    await expect(page.getByRole("status").filter({ hasText: "Cash received: 400.00 ETB. Receipt issued." })).toBeVisible();
    await expect(page.getByText(/RCT-\d{6}/).first()).toBeVisible();

    // Mobile money invoice: filling 1,800.00 — an attempt is not a payment.
    await page.getByLabel("Service", { exact: true }).selectOption({ label: "Composite filling (one surface) — 1,800.00 ETB" });
    await page.getByRole("button", { name: "Add service" }).click();
    await page.getByRole("button", { name: /Prepare invoice \(1,800\.00 ETB\)/ }).click();
    await expect(page.getByRole("status").filter({ hasText: "Invoice prepared." })).toBeVisible();
    await page.getByRole("article", { name: "Invoice draft" }).first().getByRole("button", { name: "Issue invoice" }).click();
    await expect(page.getByRole("status").filter({ hasText: "Invoice issued." })).toBeVisible();
    const mobileInvoice = page.getByRole("article").filter({ hasText: "Balance due" }).filter({ hasText: "1,800.00 ETB" }).first();
    await mobileInvoice.getByLabel("Paid by").selectOption("MobileMoney");
    await mobileInvoice.getByLabel("Provider reference").fill(reference);
    await mobileInvoice.getByRole("button", { name: "Record payment attempt" }).click();
    await expect(page.getByRole("status").filter({ hasText: "It counts only when the provider confirms it." })).toBeVisible();
    await expect(page.getByText("Waiting for provider").first()).toBeVisible();
    await expectNoSeriousA11yViolations(page, "billing");
    await page.screenshot({ path: `${SHOTS}/10-billing.png`, fullPage: true });
  });

  test("finance supervisor confirms the provider payment in the sandbox and sees a balanced ledger and the money trace", async ({ page }) => {
    await login(page, "finance.a");
    await expect(page.getByRole("heading", { name: "Finance", exact: true })).toBeVisible();
    await expect(page.getByRole("button", { name: "Billing" })).toHaveCount(0); // no cash handling for the supervisor
    await page.getByRole("tab", { name: "Drawers & settlements" }).click();
    await page.getByRole("button", { name: `Simulate provider confirmation for ${reference}` }).click();
    await expect(page.getByRole("status").filter({ hasText: "Provider confirmation simulated." })).toBeVisible();
    await expect(page.getByRole("table", { name: "Unsettled electronic payments" })).toContainText(reference);

    await page.getByRole("tab", { name: "Ledger & trace" }).click();
    await expect(page.getByText("Debits equal credits")).toBeVisible();
    const invoiceOption = page.getByLabel("Invoice", { exact: true }).locator("option", { hasText: "1,800.00 ETB (Paid)" }).first();
    await page.getByLabel("Invoice", { exact: true }).selectOption((await invoiceOption.getAttribute("value"))!);
    const trace = page.getByRole("list", { name: /Money trace for INV-/ });
    await expect(trace).toContainText("Electronic payment");
    await expect(trace).toContainText("Dr 1020 Payment provider clearing / Cr 1100 Patient accounts receivable");
    await expectNoSeriousA11yViolations(page, "finance-ledger");
  });

  test("day close: cashier counts, finance submits, the owner approves and reads the report", async ({ page, browser }) => {
    test.skip(!disposable, "locks the business day; only on a disposable database");
    // Cashier counts the drawer (float 1,000 + 400 cash).
    const cctx = await browser.newContext();
    const c = await cctx.newPage();
    await login(c, "cashier.a");
    const drawer = c.getByRole("region", { name: /My cash drawer/ });
    await drawer.getByLabel("Counted cash (ETB)").fill("1400");
    await drawer.getByRole("button", { name: "Count and close" }).click();
    await expect(drawer.getByText(/Last drawer closed: counted 1,400\.00 ETB, difference 0\.00 ETB/)).toBeVisible();
    await cctx.close();

    await login(page, "finance.a");
    await expect(page.getByRole("group", { name: "Close figures" })).toContainText("2,200.00");
    await expect(page.getByRole("list", { name: "Close checks" })).toContainText("not yet settled by the provider");
    await page.screenshot({ path: `${SHOTS}/11-day-close.png`, fullPage: true });
    await page.getByRole("button", { name: "Submit day for approval" }).click();
    await expect(page.getByRole("status").filter({ hasText: "A different person must now approve it." })).toBeVisible();
    // The submitter cannot approve their own close.
    page.once("dialog", (d) => void d.accept());
    await page.getByRole("button", { name: /^Approve and lock DC-/ }).click();
    await expect(page.getByRole("alert")).toContainText("different person");
    await expectNoSeriousA11yViolations(page, "finance-close");

    const octx = await browser.newContext();
    const o = await octx.newPage();
    await login(o, "owner.a");
    await o.getByRole("button", { name: "Finance" }).click();
    o.once("dialog", (d) => void d.accept());
    await o.getByRole("button", { name: /^Approve and lock DC-/ }).click();
    await expect(o.getByRole("status").filter({ hasText: "Day locked." })).toBeVisible();
    await o.getByRole("button", { name: "Owner reports" }).click();
    const report = o.getByRole("region", { name: /^Daily close DC-/ }).first();
    await expect(report).toContainText("Locked · with open items");
    await expect(report.getByRole("group", { name: "Close figures" })).toContainText("2,200.00");
    await expect(report.locator("pre.digest")).toContainText("VERIFIED PATIENT COLLECTIONS");
    await expect(report.locator("pre.digest")).not.toContainText("Selam"); // no patient information in the digest
    await expectNoSeriousA11yViolations(o, "owner-reports");
    await o.screenshot({ path: `${SHOTS}/12-owner-report.png`, fullPage: true });
    await octx.close();
  });
});
