import { expect, test } from "@playwright/test";

test("chat to admin inventory and back preserves conversation", async ({ page }) => {
  await page.goto("/");
  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Hello from synthetic.")).toBeVisible({ timeout: 15_000 });
  await expect(page).toHaveURL(/\/c\/[0-9a-f-]{36}$/i);
  const chatUrl = page.url();

  await page.getByRole("button", { name: "Open Admin" }).click();
  await expect(page).toHaveURL(/\/admin$/);
  await expect(page.getByRole("heading", { name: "Admin" })).toBeVisible();
  await expect(page.getByText(/examiner/i).first()).toBeVisible({ timeout: 15_000 });

  await page.getByRole("button", { name: "Return to last chat" }).click();
  await expect(page).toHaveURL(chatUrl);
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await expect(page.locator(".conversation-scroll").getByText("Hello from synthetic.")).toBeVisible();
});
