import { expect, test, type Page } from "@playwright/test";

async function chooseModel(page: Page, title: string): Promise<void> {
  await page.getByRole("button", { name: "Model" }).click();
  await page.getByTitle(title).click();
}

async function expectNoMarkerSyntax(page: Page): Promise<void> {
  await expect(page.getByText("[[speech:")).toHaveCount(0);
  await expect(page.getByText("[[md:")).toHaveCount(0);
  await expect(page.getByText("[[attachment:")).toHaveCount(0);
  await expect(page.getByText("[[artifact:")).toHaveCount(0);
}

test("structured Scripted Beta finalizes then shows same speech without a duplicate section", async ({
  page
}) => {
  await page.goto("/");
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await chooseModel(page, "Scripted Beta");
  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Hello from synthetic.")).toBeVisible({ timeout: 15_000 });
  await expect(page.getByText("Finalizing response…")).toHaveCount(0);
  await expect(page.locator(".agent-activity")).toHaveCount(0);
  await expect(page.locator(".spoken-text")).toHaveCount(0);
  await expect(page.getByLabel("Speech text")).toHaveCount(0);
  await expect(page.getByLabel("Spoken")).toHaveCount(0);
  await expectNoMarkerSyntax(page);

  await page.reload();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await expect(page.getByText("Hello from synthetic.")).toBeVisible();
  await expect(page.locator(".spoken-text")).toHaveCount(0);
  await expect(page.locator(".agent-activity")).toHaveCount(0);
});

test("structured Scripted Beta none omits secondary speech and custom still uses Speech text", async ({
  page
}) => {
  await page.goto("/");
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await chooseModel(page, "Scripted Beta");
  await page.getByLabel("Message").fill("[test:speech-none]");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Shown only.")).toBeVisible({ timeout: 15_000 });
  await expect(page.locator(".spoken-text")).toHaveCount(0);
  await expect(page.getByLabel("Speech text")).toHaveCount(0);
  await expect(page.getByLabel("Spoken")).toHaveCount(0);

  await page.getByLabel("Message").fill("[test:rich-envelope]");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Shown display.")).toBeVisible({ timeout: 15_000 });
  await expect(page.getByLabel("Speech text")).toHaveCount(1);
  await expect(page.locator(".spoken-text")).toContainText("Hidden speech");
  await expect(page.getByLabel("Spoken")).toHaveCount(0);
  await expectNoMarkerSyntax(page);
});

test("compatibility Scripted Alpha converges without visible marker syntax", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Hello from synthetic.")).toBeVisible({ timeout: 15_000 });
  await expect(page.getByText("Finalizing response…")).toHaveCount(0);
  await expectNoMarkerSyntax(page);
});
