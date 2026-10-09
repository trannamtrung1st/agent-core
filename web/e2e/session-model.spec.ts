import { INSTANCE_DEFINITIONS, selectInstanceIdentity } from "./support/instance-identity";
import { expect, test, type Page } from "@playwright/test";

async function startTextSession(page: Page, text: string): Promise<void> {
  await page.getByLabel("Message").fill(text);
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Hello from synthetic.")).toBeVisible({ timeout: 15_000 });
}

async function chooseModel(page: Page, title: string): Promise<void> {
  await page.getByRole("button", { name: "Model", exact: true }).click();
  await page.getByTitle(title).click();
}

test("session model selection is isolated between chats", async ({ page }) => {
  const stamp = Date.now();
  const textA = `Isolation A ${stamp}`;
  const textB = `Isolation B ${stamp}`;

  await page.goto("/");
  await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.examiner);
  await expect(page.getByRole("button", { name: "Model", exact: true })).toBeVisible();
  await expect(page.getByLabel("Reasoning effort")).toBeVisible();
  await page.getByRole("button", { name: "Model", exact: true }).click();
  const catalog = page.getByRole("listbox");
  await expect(catalog.getByTitle("Scripted Alpha")).toHaveCount(1);
  await expect(catalog.getByTitle("Scripted Beta")).toHaveCount(1);
  await expect(catalog.getByText("Default", { exact: true })).toHaveCount(1);
  await expect(page.locator(".model-picker")).toContainText("Default");
  await page.keyboard.press("Escape");

  await chooseModel(page, "Scripted Beta");
  await expect(page.getByLabel("Reasoning effort")).toHaveCount(0);
  await startTextSession(page, textA);
  await expect(page.getByRole("button", { name: "Model", exact: true })).toBeVisible();
  await expect(page.locator(".model-picker")).toContainText("Scripted Beta");
  await expect(page.getByLabel("Reasoning effort")).toHaveCount(0);
  const sessionA = page.url();

  await page.getByRole("button", { name: "Start a new chat" }).click();
  await expect(page.getByLabel("Identity")).toBeVisible({ timeout: 15_000 });
  await expect(page.getByRole("button", { name: "Model", exact: true })).toBeVisible();
  await expect(page.locator(".model-picker")).toContainText("Default");
  await startTextSession(page, textB);
  await expect(page.locator(".model-picker")).toContainText("Scripted Alpha");
  await expect(page.getByLabel("Reasoning effort")).toBeVisible();
  const sessionB = page.url();

  await page.goto(sessionA);
  await expect(page.locator(".conversation-list").getByText(textA)).toBeVisible({ timeout: 15_000 });
  await expect(page.locator(".model-picker")).toContainText("Scripted Beta");
  await expect(page.getByLabel("Reasoning effort")).toHaveCount(0);

  await page.goto(sessionB);
  await expect(page.locator(".conversation-list").getByText(textB)).toBeVisible({ timeout: 15_000 });
  await expect(page.locator(".model-picker")).toContainText("Scripted Alpha");
  await expect(page.getByLabel("Reasoning effort")).toBeVisible();
});

test("descending catalog efforts keep keyboard direction and durable wire selection", async ({ page }) => {
  await page.route("**/api/v2/models", async route => {
    const response = await route.fetch();
    const catalog = await response.json();
    for (const model of catalog.models) model.supportedReasoningEfforts.reverse();
    await route.fulfill({ response, json: catalog });
  });
  await page.goto("/");
  await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.examiner);
  await page.getByRole("button", { name: "Model", exact: true }).click();
  const slider = page.getByRole("slider");
  await slider.press("Home");
  await expect(slider).toHaveAttribute("aria-valuetext", "Low");
  await slider.press("ArrowRight");
  await expect(slider).toHaveAttribute("aria-valuetext", "Medium");
  await slider.press("ArrowUp");
  await expect(slider).toHaveAttribute("aria-valuetext", "High");
  await slider.press("ArrowLeft");
  await expect(slider).toHaveAttribute("aria-valuetext", "Medium");
  await slider.press("ArrowDown");
  await expect(slider).toHaveAttribute("aria-valuetext", "Low");
  await slider.press("End");
  await expect(slider).toHaveAttribute("aria-valuetext", "High");
  await page.keyboard.press("Escape");
  await startTextSession(page, "Verify ascending reasoning and saved selection");
  const sessionUrl = page.url();
  const token = await page.evaluate(() => localStorage.getItem("agent-core.owner-capability"));
  const sessionId = sessionUrl.split("/").pop();
  const response = await page.request.get(`/api/v2/sessions/${sessionId}`, { headers: { "X-AgentCore-Owner-Capability": token! } });
  expect(response.ok()).toBe(true);
  expect((await response.json()).model.reasoningEffort).toBe("high");
  await page.reload();
  await expect(page.getByLabel("Reasoning effort")).toHaveText("High");
});
