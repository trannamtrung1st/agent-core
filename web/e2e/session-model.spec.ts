import { expect, test, type Page } from "@playwright/test";

async function startTextSession(page: Page, text: string): Promise<void> {
  await page.getByLabel("Message").fill(text);
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Hello from synthetic.")).toBeVisible({ timeout: 15_000 });
}

async function chooseModel(page: Page, title: string): Promise<void> {
  await page.getByLabel("Model").click();
  await page.getByTitle(title).click();
}

test("session model selection is isolated between chats", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByLabel("Model")).toBeVisible();
  await expect(page.getByLabel("Reasoning")).toBeVisible();
  await expect(page.locator(".model-picker")).toContainText("Default");

  await chooseModel(page, "Scripted Beta");
  await expect(page.getByLabel("Reasoning")).toHaveCount(0);
  await startTextSession(page, "Session A");
  await expect(page.getByLabel("Model")).toBeVisible();
  await expect(page.locator(".model-picker")).toContainText("Scripted Beta");
  await expect(page.getByLabel("Reasoning")).toHaveCount(0);
  const sessionA = page.url();

  await page.getByRole("button", { name: "Start a new chat" }).click();
  await expect(page.getByLabel("Identity")).toBeVisible({ timeout: 15_000 });
  await expect(page.getByLabel("Model")).toBeVisible();
  await expect(page.locator(".model-picker")).toContainText("Default");
  await startTextSession(page, "Session B");
  await expect(page.locator(".model-picker")).toContainText("Scripted Alpha");
  await expect(page.getByLabel("Reasoning")).toBeVisible();
  const sessionB = page.url();

  await page.goto(sessionA);
  await expect(page.getByText("Session A")).toBeVisible({ timeout: 15_000 });
  await expect(page.locator(".model-picker")).toContainText("Scripted Beta");
  await expect(page.getByLabel("Reasoning")).toHaveCount(0);

  await page.goto(sessionB);
  await expect(page.getByText("Session B")).toBeVisible({ timeout: 15_000 });
  await expect(page.locator(".model-picker")).toContainText("Scripted Alpha");
  await expect(page.getByLabel("Reasoning")).toBeVisible();
});
