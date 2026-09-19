import { expect, test, type Page } from "@playwright/test";

async function startTextSession(page: Page, text: string): Promise<void> {
  await page.getByLabel("Message").fill(text);
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Hello from synthetic.")).toBeVisible({ timeout: 15_000 });
}

async function chooseModel(page: Page, title: string): Promise<void> {
  await page.getByRole("button", { name: "Model" }).click();
  await page.getByTitle(title).click();
}

test("session model selection is isolated between chats", async ({ page }) => {
  const stamp = Date.now();
  const textA = `Isolation A ${stamp}`;
  const textB = `Isolation B ${stamp}`;

  await page.goto("/");
  await expect(page.getByRole("button", { name: "Model" })).toBeVisible();
  await expect(page.getByLabel("Reasoning")).toBeVisible();
  await page.getByRole("button", { name: "Model" }).click();
  const catalog = page.getByRole("listbox");
  await expect(catalog.getByTitle("Scripted Alpha")).toHaveCount(1);
  await expect(catalog.getByTitle("Scripted Beta")).toHaveCount(1);
  await expect(catalog.getByText("Default", { exact: true })).toHaveCount(1);
  await expect(page.locator(".model-picker")).toContainText("Default");
  await page.keyboard.press("Escape");

  await chooseModel(page, "Scripted Beta");
  await expect(page.getByLabel("Reasoning")).toHaveCount(0);
  await startTextSession(page, textA);
  await expect(page.getByRole("button", { name: "Model" })).toBeVisible();
  await expect(page.locator(".model-picker")).toContainText("Scripted Beta");
  await expect(page.getByLabel("Reasoning")).toHaveCount(0);
  const sessionA = page.url();

  await page.getByRole("button", { name: "Start a new chat" }).click();
  await expect(page.getByLabel("Identity")).toBeVisible({ timeout: 15_000 });
  await expect(page.getByRole("button", { name: "Model" })).toBeVisible();
  await expect(page.locator(".model-picker")).toContainText("Default");
  await startTextSession(page, textB);
  await expect(page.locator(".model-picker")).toContainText("Scripted Alpha");
  await expect(page.getByLabel("Reasoning")).toBeVisible();
  const sessionB = page.url();

  await page.goto(sessionA);
  await expect(page.locator(".conversation-list").getByText(textA)).toBeVisible({ timeout: 15_000 });
  await expect(page.locator(".model-picker")).toContainText("Scripted Beta");
  await expect(page.getByLabel("Reasoning")).toHaveCount(0);

  await page.goto(sessionB);
  await expect(page.locator(".conversation-list").getByText(textB)).toBeVisible({ timeout: 15_000 });
  await expect(page.locator(".model-picker")).toContainText("Scripted Alpha");
  await expect(page.getByLabel("Reasoning")).toBeVisible();
});
