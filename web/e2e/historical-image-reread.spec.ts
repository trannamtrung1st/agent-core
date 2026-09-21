import { expect, test } from "@playwright/test";

const HISTORICAL_IMAGE_REREAD_MARKER = "[test:historical-image-reread]";
const HISTORICAL_IMAGE_REREAD_ANSWER =
  "Synthetic historical image reread: observed sanitized image content.";

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

async function attachPng(page: import("@playwright/test").Page): Promise<void> {
  await page.locator("input.attach-input").setInputFiles({
    name: "photo.png",
    mimeType: "image/png",
    buffer: pngBuffer()
  });
  await expect(page.locator(".attach-list")).toContainText("photo.png", { timeout: 15_000 });
}

test("scripted vision completes a later-turn historical image reread", async ({ page }) => {
  test.setTimeout(120_000);
  await page.goto("/");
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });

  await chooseModel(page, "Scripted Vision");
  await attachPng(page);
  await page.getByLabel("Message").fill("First turn with photo");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.locator(".conversation-list").getByText("Hello from synthetic.").last()).toBeVisible({
    timeout: 30_000
  });

  await page.getByLabel("Message").fill(
    `${HISTORICAL_IMAGE_REREAD_MARKER} Please inspect the earlier image.`
  );
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.locator(".chat-message-assistant").last()).toContainText(HISTORICAL_IMAGE_REREAD_ANSWER, {
    timeout: 60_000
  });
});

test("tools-capable non-vision model does not claim historical image sight", async ({ page }) => {
  test.setTimeout(120_000);
  await page.goto("/");
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });

  await chooseModel(page, "Scripted Vision");
  await attachPng(page);
  await page.getByLabel("Message").fill("First turn with photo");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.locator(".conversation-list").getByText("Hello from synthetic.").last()).toBeVisible({
    timeout: 30_000
  });

  await chooseModel(page, "Scripted Alpha");
  await page.getByLabel("Message").fill(
    `${HISTORICAL_IMAGE_REREAD_MARKER} Please inspect the earlier image.`
  );
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.locator(".chat-message-assistant").last()).toBeVisible({ timeout: 60_000 });
  await expect(page.locator(".chat-message-assistant").last()).not.toContainText(HISTORICAL_IMAGE_REREAD_ANSWER);
});
