import { expect, test } from "@playwright/test";
import { eatTimeSoon, expectNoSeriousA11yViolations, login, SHOTS } from "./helpers";

// Synthetic, unique patient per run.
const suffix = Date.now().toString(36).slice(-5);
const given = `Hiwot${suffix}`;
const father = `Synthetic${suffix}`;

test.describe.serial("Vertical slice: login → registration → appointment → encounter → sign → preview → final PDF", () => {
  let encounterUrl = "";

  test("receptionist registers, books and checks in a patient", async ({ page }) => {
    await login(page, "reception.a");
    await expect(page.getByRole("heading", { name: "Reception" })).toBeVisible();

    await page.getByLabel("Given name", { exact: true }).fill(given);
    await page.getByLabel("Father's name", { exact: true }).fill(father);
    await page.getByLabel("Grandfather's name").fill("Testgrand");
    await page.getByLabel("Sex", { exact: true }).selectOption("Female");
    await page.getByLabel("Date of birth (Gregorian)").fill("1993-07-21");
    await expect(page.getByText("Hamle 14, 1985 E.C.")).toBeVisible(); // both calendars shown, labelled
    await page.getByRole("button", { name: "Register patient" }).click();
    await expect(page.getByRole("status").filter({ hasText: "registered as MRN-" })).toBeVisible();

    const slot = eatTimeSoon(10);
    await page.getByLabel("Clinician").selectOption({ label: "Dr. Abebe Kassa (synthetic) — General Practice" });
    await page.getByLabel("Date (Gregorian)", { exact: true }).fill(slot.date);
    await page.getByLabel("Time (EAT, UTC+3)").fill(slot.time);
    await page.getByRole("button", { name: "Book", exact: true }).click();
    await expect(page.getByText(/Booked .* with Dr. Abebe/)).toBeVisible();

    const row = page.getByRole("row").filter({ hasText: given });
    await row.getByRole("button", { name: /Check in/ }).click();
    const dialog = page.getByRole("dialog", { name: "Confirm patient identity" });
    await expect(dialog.getByText("1993-07-21 (Gregorian)")).toBeVisible();
    await expect(dialog.getByRole("button", { name: "Check in" })).toBeDisabled(); // identity must be confirmed first
    await dialog.getByLabel(/I confirmed the patient's name/).check();
    await dialog.getByRole("button", { name: "Check in" }).click();
    await expect(row.getByText("Checked in")).toBeVisible();
    await expectNoSeriousA11yViolations(page, "reception");
    await page.screenshot({ path: `${SHOTS}/01-reception.png`, fullPage: true });
  });

  test("clinician records allergy status, drafts, signs, previews and finalizes", async ({ page }) => {
    await login(page, "dr.abebe");
    await page.getByRole("button", { name: `Open encounter for ${given} ${father} Testgrand` }).click();
    const banner = page.getByRole("region", { name: "Patient identity and safety" });
    await expect(banner).toContainText(given);
    await expect(banner.getByText("? Allergy status not recorded")).toBeVisible();
    encounterUrl = page.url();

    await page.getByRole("button", { name: "Record “no known allergies”" }).click();
    await expect(banner.getByText("✓ No known allergies")).toBeVisible();

    await page.getByLabel(/Presenting complaint/).fill("Sore throat for 2 days (synthetic)");
    await page.getByLabel("Examination", { exact: true }).fill("Mild pharyngeal erythema. Afebrile.");
    await page.getByLabel(/Assessment/).fill("Viral pharyngitis (synthetic scenario)");
    await page.getByLabel("Plan", { exact: true }).fill("Supportive care. Return if worse.");
    await page.getByRole("button", { name: "Save draft" }).click();
    await expect(page.getByText(/Draft saved \(version \d+\)\. Not signed\./)).toBeVisible();
    await expectNoSeriousA11yViolations(page, "encounter-draft");
    await page.screenshot({ path: `${SHOTS}/02-encounter-draft.png`, fullPage: true });

    await page.getByRole("button", { name: "Review and sign…" }).click();
    const dialog = page.getByRole("dialog", { name: "Sign consultation note" });
    await expect(dialog).toContainText(given);
    await expect(dialog.getByRole("button", { name: "Sign note" })).toBeDisabled();
    await dialog.getByLabel(/reviewed this patient's allergies/).check();
    await dialog.getByRole("button", { name: "Sign note" }).click();

    const signed = page.getByRole("article", { name: "Signed note" });
    await expect(signed).toContainText("Viral pharyngitis");
    await expect(signed.getByText("✓ integrity verified")).toBeVisible();
    await expect(page.getByLabel(/Presenting complaint/)).toHaveCount(0); // no editable draft after signing

    // Automation prepares the draft summary after signing (outbox worker); the clinician only reviews and finalizes.
    const autoRow = page.getByRole("row").filter({ hasText: "Prepared automatically" });
    await expect(autoRow).toBeVisible({ timeout: 15_000 });
    // Finalizing is only offered next to the visible preview (no one-click finalize from the list).
    await expect(autoRow.getByRole("button", { name: /^Finalize/ })).toHaveCount(0);
    await autoRow.getByRole("button", { name: /^Preview draft/ }).click();
    await expect(page.getByText("Draft preview — watermarked, not valid for clinical or legal use.")).toBeVisible();
    await expect(page.getByTitle("Document preview")).toBeVisible();
    await page.screenshot({ path: `${SHOTS}/03-document-preview.png`, fullPage: true });

    let confirmText = "";
    page.once("dialog", (d) => { confirmText = d.message(); void d.accept(); });
    await page.getByRole("button", { name: "Finalize this document" }).click();
    await expect(page.getByText("Final document (immutable).")).toBeVisible();
    expect(confirmText).toContain(given); // the irreversible step names the patient
    await expect(page.getByRole("row").filter({ hasText: "Consultation summary" }).getByText("✓ Final")).toBeVisible();

    // The final PDF is served only to the authenticated session, with integrity headers.
    const docs = await page.request.get(`/api/v1/documents?encounterId=${encounterUrl.split("/").pop()}`);
    const doc = ((await docs.json()) as { id: string; status: string; pdfSha256: string; snapshotSha256: string }[]).find((d) => d.status === "Final");
    const pdf = await page.request.get(`/api/v1/documents/${doc!.id}/download`);
    expect(pdf.status()).toBe(200);
    expect(pdf.headers()["content-type"]).toBe("application/pdf");
    expect(pdf.headers()["x-pdf-sha256"]).toBe(doc!.pdfSha256);
    expect((await pdf.body()).subarray(0, 5).toString()).toBe("%PDF-");

    await page.getByRole("button", { name: "Print…" }).click();
    await expect(page.getByText("The system cannot confirm paper was printed")).toBeVisible();
    await expectNoSeriousA11yViolations(page, "encounter-signed");
    await page.screenshot({ path: `${SHOTS}/04-encounter-signed-final.png`, fullPage: true });

    // Addendum keeps the original intact.
    await page.getByLabel("Reason for addendum").fill("Throat swab result reviewed");
    await page.getByLabel("Addendum text").fill("Rapid strep negative (synthetic).");
    await page.getByRole("button", { name: "Sign addendum" }).click();
    await expect(page.getByRole("article", { name: "Addendum 1" })).toContainText("Rapid strep negative");
    await expect(page.getByRole("article", { name: "Signed note" })).toContainText("Viral pharyngitis");
  });

  test("an unrelated tenant cannot open the encounter, and an unauthenticated browser gets the sign-in page", async ({ page, browser }) => {
    expect(encounterUrl).not.toBe("");
    await login(page, "dr.b");
    await page.goto(encounterUrl);
    await expect(page.getByRole("alert")).toContainText(/not found/i);
    await expect(page.getByText(given)).toHaveCount(0);

    const anon = await browser.newContext();
    const p2 = await anon.newPage();
    await p2.goto(encounterUrl);
    await expect(p2.getByRole("button", { name: "Sign in" })).toBeVisible();
    const api = await p2.request.get(`/api/v1/encounters/${encounterUrl.split("/").pop()}`);
    expect(api.status()).toBe(401);
    await anon.close();
  });

  test("login page is keyboard operable and accessible", async ({ page }) => {
    await page.goto("/");
    await expectNoSeriousA11yViolations(page, "login");
    await page.keyboard.press("Tab");
    await expect(page.getByLabel("Username")).toBeFocused();
    await page.keyboard.type("nurse.a");
    await page.keyboard.press("Tab");
    await page.keyboard.type("wrong-password");
    await page.keyboard.press("Enter");
    await expect(page.getByRole("alert")).toContainText("Invalid username or password");
    await page.screenshot({ path: `${SHOTS}/00-login-error.png` });
  });
});
