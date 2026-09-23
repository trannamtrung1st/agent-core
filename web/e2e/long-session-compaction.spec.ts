import { expect, test } from "@playwright/test";
import { waitForResponseSettled } from "./support/response-settled";

test("long session recalls an early fact after compaction", async ({ page }) => {
  test.setTimeout(180_000);
  await page.goto("/");
  await send(page, "Please remember P4A_LONG_FACT for later.");
  for (let index = 0; index < 22; index += 1) {
    await send(page, `continue ${index}`);
  }

  await send(page, "What is the remembered code word?");
  await expect(page.locator(".chat-message-assistant").last()).toContainText("P4A_LONG_FACT");
  await expect(
    page.locator(".conversation-scroll").getByText("Please remember P4A_LONG_FACT for later.", { exact: true })
  ).toBeVisible();

  await page.getByRole("button", { name: "Conversation actions" }).click();
  await page.getByRole("menuitem", { name: "End" }).click();
  await expect(page.getByTestId("connection")).toHaveText("Ended", { timeout: 15_000 });
});

async function send(page: import("@playwright/test").Page, text: string): Promise<void> {
  const composer = page.getByRole("textbox", { name: "Message", exact: true });
  await composer.fill(text);
  await page.getByRole("button", { name: "Send", exact: true }).click();
  await expect(page.locator(".conversation-scroll").getByText(text, { exact: true })).toBeVisible({
    timeout: 30_000
  });
  await expect(page.getByRole("region", { name: "Queued messages" })).toHaveCount(0);
  await waitForResponseSettled(page);
}
