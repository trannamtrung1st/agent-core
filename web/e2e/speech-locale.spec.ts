import { expect, test } from "@playwright/test";

test("speech locale override does not rewrite text chat", async ({ page }) => {
  await page.goto("/");
  await page.getByLabel("Speech locale").click();
  await page.getByTitle("French (fr-FR)").click();
  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Hello from synthetic.")).toBeVisible({ timeout: 15_000 });
  await expect(page.locator(".speech-locale-picker")).toContainText("French");

  await page.getByLabel("Message").fill("Still text");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Still text")).toBeVisible({ timeout: 15_000 });
  await expect(page.getByLabel("Message")).toBeEnabled();

  await page.getByLabel("Speech locale").click();
  await page.getByTitle("Agent default").click();
  await expect(page.locator(".speech-locale-picker")).toContainText("Agent default");
  await expect(page.getByRole("button", { name: "Voice" })).toBeVisible();
});

test("new chat speech locale is available before the first send", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByLabel("Identity")).toBeVisible();
  await expect(page.getByLabel("Speech locale")).toBeVisible();
  await expect(page.getByLabel("Message")).toBeEnabled();
});
