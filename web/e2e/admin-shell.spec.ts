import { expect, test } from "@playwright/test";

async function startSyntheticChat(page: import("@playwright/test").Page) {
  await page.goto("/");
  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Hello from synthetic.")).toBeVisible({ timeout: 15_000 });
  await expect(page).toHaveURL(/\/c\/[0-9a-f-]{36}$/i);
}

async function openFirstInstanceEffectiveConfig(page: import("@playwright/test").Page) {
  await page.getByRole("button", { name: "Open Admin" }).click();
  await expect(page).toHaveURL(/\/admin$/);
  await expect(page.getByRole("heading", { name: "Admin" })).toBeVisible();
  const instanceLink = page.locator('section[aria-label="Instances"] button').first();
  await expect(instanceLink).toBeVisible({ timeout: 15_000 });
  await instanceLink.click();
  await expect(page).toHaveURL(/\/admin\/instances\/[0-9a-f-]{36}$/i);
  await expect(page.getByText("Effective model")).toBeVisible();
  await expect(page.getByText("Offered tools")).toBeVisible();
}

test("chat to admin effective config and back with a new turn", async ({ page }) => {
  const consoleErrors: string[] = [];
  const failedRequests: string[] = [];
  page.on("console", (message) => {
    if (message.type() === "error") {
      consoleErrors.push(message.text());
    }
  });
  page.on("requestfailed", (request) => {
    failedRequests.push(`${request.method()} ${request.url()}`);
  });

  await startSyntheticChat(page);
  const chatUrl = page.url();

  await openFirstInstanceEffectiveConfig(page);
  await expect(page.getByText(/Compatibility \/ legacy|Managed/).first()).toBeVisible();
  await expect(page.getByText("Durable work eligibility")).toBeVisible();

  await page.getByRole("button", { name: "Return to last chat" }).click();
  await expect(page).toHaveURL(chatUrl);
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });

  await page.getByLabel("Message").fill("Second turn");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Hello from synthetic.")).toBeVisible();
  await expect(page.locator(".conversation-scroll").getByText("Second turn", { exact: true })).toBeVisible();

  expect(failedRequests.filter((item) => !item.includes("favicon"))).toEqual([]);
  expect(consoleErrors.filter((line) => !line.includes("[antd: List]"))).toEqual([]);
});

test("admin effective config at narrow width", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await startSyntheticChat(page);
  await openFirstInstanceEffectiveConfig(page);
  await expect(page.getByText("Harness references")).toBeVisible();
  await page.getByRole("button", { name: "Return to last chat" }).click();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
});
