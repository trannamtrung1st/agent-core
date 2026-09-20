import { expect, test } from "@playwright/test";

function pngBuffer(): Buffer {
  return Buffer.from(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==",
    "base64"
  );
}

async function chooseModel(page: import("@playwright/test").Page, title: string): Promise<void> {
  await page.getByRole("button", { name: "Model" }).click();
  await page.getByTitle(title).click();
}

test("vision incompatibility blocks send until Scripted Vision is selected", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });

  await page.getByLabel("Message").fill("Warm up");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Hello from synthetic.")).toBeVisible({ timeout: 15_000 });

  await page.locator("input.attach-input").setInputFiles({
    name: "photo.png",
    mimeType: "image/png",
    buffer: pngBuffer()
  });
  await expect(page.locator(".attach-list")).toContainText("photo.png", { timeout: 15_000 });

  await expect(page.getByRole("alert").filter({ hasText: "This model cannot read images" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Send" })).toBeDisabled();

  await chooseModel(page, "Scripted Vision");
  await expect(page.getByRole("alert").filter({ hasText: "This model cannot read images" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Send" })).toBeEnabled();

  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.locator(".conversation-list").getByText("Hello from synthetic.").last()).toBeVisible({
    timeout: 15_000
  });
});
