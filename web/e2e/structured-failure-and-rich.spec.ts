import { expect, test } from "@playwright/test";

test("validation failures keep structured recoverable details", async ({ page }) => {
  await page.goto("/");
  await page.getByLabel("Message").fill("x".repeat(8001));
  await page.getByRole("button", { name: "Send" }).click();
  const alert = page.getByTestId("session-failure");
  await expect(alert).toBeVisible({ timeout: 15_000 });
  await expect(alert).toHaveAttribute("data-error-class", "validation/protocol");
  await expect(alert).toHaveAttribute("data-error-code", "ValidationError");
  await expect(alert).toHaveAttribute("data-error-fatal", "false");
  await expect(alert).toContainText("Text exceeds 8000 UTF-16 code units.");
  await page.getByRole("button", { name: "Failure details" }).click();
  await expect(page.getByTestId("session-failure-details")).toContainText("Recoverable");
  await expect(page.getByText("sk-")).toHaveCount(0);
});

test("rich envelope shows differing spoken text without another message and does not replay thinking after reload", async ({ page }) => {
  await page.goto("/");
  await page.getByLabel("Message").fill("[test:rich-envelope]");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Shown display.")).toBeVisible({ timeout: 15_000 });
  await expect(page.getByText("Extra block")).toBeVisible();
  await expect(page.getByText("fixture-attachment-1")).toBeVisible();
  await expect(page.getByRole("button", { name: /Artifact fixture-artifact-1/ })).toBeVisible();
  await expect(page.getByText("[Unsupported content]")).toBeVisible();
  await expect(page.locator(".spoken-text")).toContainText("Hidden speech");
  // Text-delivered speech projects as "Speech text"; "Spoken" is reserved for voice delivery.
  await expect(page.getByLabel("Speech text")).toHaveCount(1);
  await expect(page.locator(".chat-message-assistant")).toHaveCount(1);
  await expect(page.getByText("Thinking…")).toHaveCount(0);

  await page.reload();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await expect(page.getByText("Shown display.")).toBeVisible();
  await expect(page.locator(".spoken-text")).toContainText("Hidden speech");
  await expect(page.getByLabel("Speech text")).toHaveCount(1);
  await expect(page.getByText("Thinking…")).toHaveCount(0);
});
