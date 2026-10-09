import { backgroundFixture, mockBackgroundSessions } from './support/background-fixtures';
import { INSTANCE_DEFINITIONS, selectInstanceIdentity } from "./support/instance-identity";
import { expect, test } from "@playwright/test";

test.use({ permissions: ["clipboard-read", "clipboard-write"] });

test("failed assistant details copy and survive reload", async ({ page }) => {
  await page.goto("/");
  await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.examiner);
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await page.getByLabel("Message").fill("synthetic-fail-turn");
  await page.getByRole("button", { name: "Send" }).click();
  const row = page.locator(".chat-message-assistant").last();
  await expect(row.getByText("Failed")).toBeVisible({ timeout: 15_000 });
  await row.getByRole("button", { name: "Failed — show error details" }).click();
  const details = page.getByTestId("diagnostic-details");
  await expect(details).toContainText("Diagnostic ID");
  const diagnosticId = (await page.getByTestId("diagnostic-id").innerText()).trim();
  expect(diagnosticId).toMatch(/^[0-9a-f-]{36}$/i);
  await page.getByRole("button", { name: "Copy details" }).click();
  await expect(page.getByRole("status")).toHaveText("Copied");
  const copied = await page.evaluate(() => navigator.clipboard.readText());
  expect(copied).toContain(`Diagnostic ID: ${diagnosticId}`);
  expect(copied).not.toContain("synthetic-fail-turn");

  await page.reload();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  const reloaded = page.locator(".chat-message-assistant").last();
  await expect(reloaded.getByText("Failed")).toBeVisible();
  await reloaded.getByRole("button", { name: "Failed — show error details" }).click();
  await expect(page.getByTestId("diagnostic-id")).toHaveText(diagnosticId);
});

test("invalid tool strategy is bounded and its diagnostic survives reload", async ({ page }) => {
  await page.goto("/");
  await selectInstanceIdentity(page, { id: "general-assistant", version: 20 });
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await page.getByLabel("Message").fill("synthetic-invalid-tool-turn");
  await page.getByRole("button", { name: "Send" }).click();
  const row = page.locator(".chat-message-assistant").last();
  await expect(row.getByText("Failed")).toBeVisible({ timeout: 15_000 });
  await row.getByRole("button", { name: "Failed — show error details" }).click();
  await expect(page.getByTestId("diagnostic-reason")).toHaveText("invalidToolStrategy");
  const id = await page.getByTestId("diagnostic-id").innerText();
  await page.getByRole("button", { name: "Copy details" }).click();
  await expect(page.getByRole("status")).toHaveText("Copied");
  expect(await page.evaluate(() => navigator.clipboard.readText())).toContain("Reason: invalidToolStrategy");
  await page.reload();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await page.locator(".chat-message-assistant").last().getByRole("button", { name: "Failed — show error details" }).click();
  await expect(page.getByTestId("diagnostic-id")).toHaveText(id);
  await expect(page.getByTestId("diagnostic-reason")).toHaveText("invalidToolStrategy");
});

for (const replyFails of [false, true]) {
  test(`browser actions survive ${replyFails ? "failed" : "recovered"} finalization`, async ({ page }) => {
    await page.goto("/");
    await selectInstanceIdentity(page, { id: "general-assistant", version: 20 });
    await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
    const origin = `http://127.0.0.1:${process.env.PLAYWRIGHT_FIXTURE_PORT ?? "5091"}`;
    await page.getByLabel("Message").fill(`synthetic-finalization-${replyFails ? "fail" : "recover"} ${origin}/`);
    await page.getByRole("button", { name: "Send" }).click();
    const row = page.locator(".chat-message-assistant").last();
    await expect(row.getByText("✓ Browser closed", { exact: true })).toBeVisible({ timeout: 15_000 });
    if (replyFails) {
      await expect(row.getByText("Reply failed", { exact: true })).toBeVisible();
      await row.getByRole("button", { name: "Reply failed — show error details" }).click();
      await expect(page.getByTestId("diagnostic-reason")).toHaveText("totalTimeout");
      const id = await page.getByTestId("diagnostic-id").innerText();
      await page.reload();
      await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
      await expect(page.locator(".chat-message-assistant").last().getByText("✓ Browser closed", { exact: true })).toBeVisible();
      await page.locator(".chat-message-assistant").last().getByRole("button", { name: "Reply failed — show error details" }).click();
      await expect(page.getByTestId("diagnostic-id")).toHaveText(id);
    } else {
      await expect(row).toContainText("Sign-out was not verified.");
      await expect(row.getByText("Reply failed", { exact: true })).toHaveCount(0);
    }
  });
}

test("native sign-out confirmation completes before closure and final reply", async ({ page }) => {
  await page.goto("/");
  await selectInstanceIdentity(page, { id: "general-assistant", version: 20 });
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await page.getByLabel("Message").fill(`synthetic-browser-cleanup http://127.0.0.1:${process.env.PLAYWRIGHT_FIXTURE_PORT ?? "5091"}/`);
  await page.getByRole("button", { name: "Send" }).click();
  const row = page.locator(".chat-message-assistant").last();
  await expect(row).toContainText("Sign-out verified at the login screen", { timeout: 30_000 });
  await expect(row.getByText("✓ Browser closed", { exact: true })).toBeVisible();
  await expect(row.getByText("Reply failed", { exact: true })).toHaveCount(0);
  await page.reload();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await expect(page.locator(".chat-message-assistant").last()).toContainText("Sign-out verified at the login screen");
});

test("background work shows a seeded terminal diagnostic", async ({ page }) => {
  await page.goto("/");
  await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.examiner);
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.locator(".chat-message-assistant")).toBeVisible({ timeout: 15_000 });
  await mockBackgroundSessions(page, [backgroundFixture(170, 'Failed check', {
    agentRunId: '019944af-00c5-7000-8000-0000000000aa', status: 'failed', outcome: null,
    failureCode: 'model-timeout', failureSummary: 'The model timed out.', diagnosticId: '019944af-0008-7000-8000-0000000000d9'
  })]);
  await page.getByRole('button', { name: 'Background work', exact: true }).click();
  await page.getByRole('dialog', { name: 'Background work', exact: true }).getByRole('button', { name: 'View original result', exact: true }).click();
  const drawer = page.getByRole('dialog', { name: 'Failed check', exact: true });
  await expect(drawer.getByText('The model timed out.')).toBeVisible();
  await drawer.getByRole("button", { name: "Error details" }).click();
  const details = page.getByTestId("diagnostic-details");
  await expect(page.getByTestId("diagnostic-id")).toHaveText("019944af-0008-7000-8000-0000000000d9");
  await expect(details).toContainText("AgentRun ID");
  await expect(details).toContainText("019944af-00c5-7000-8000-0000000000aa");
  await expect(details).toContainText("model-timeout");
  await expect(details).not.toContainText("Trigger ID");
  await expect(details).not.toContainText("Occurrence ID");
});

test("admin inventory keeps a server diagnostic id", async ({ page }) => {
  await page.goto("/");
  await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.examiner);
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await page.route("**/api/v2/admin/definitions", async (route) => {
    await route.fulfill({
      status: 500,
      contentType: "application/problem+json",
      body: JSON.stringify({
        title: "Internal Server Error",
        status: 500,
        detail: "The request could not be completed.",
        diagnosticId: "019944af-0008-7000-8000-0000000000e1"
      })
    });
  });
  await page.getByRole("button", { name: "Open Admin" }).click();
  const definitions = page.locator('section[aria-label="Definitions"]');
  await expect(definitions.getByText("The request could not be completed.")).toBeVisible({ timeout: 15_000 });
  await definitions.getByRole("button", { name: "Error details" }).click();
  await expect(page.getByTestId("diagnostic-id")).toHaveText("019944af-0008-7000-8000-0000000000e1");
});

async function openNewDraftForm(page: import("@playwright/test").Page, definitionId: string) {
  await page.goto("/");
  await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.examiner);
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await page.getByRole("button", { name: "Open Admin" }).click();
  await page.getByRole("button", { name: "New definition" }).click();
  await page.getByRole("textbox", { name: "Definition ID" }).fill(definitionId);
  await page.getByRole("button", { name: "Create draft" }).click();
  await expect(page.getByRole("dialog", { name: "New definition" })).toBeHidden({ timeout: 15_000 });
  await expect(page).toHaveURL(new RegExp(`/admin/definitions/${definitionId}$`));
}

test("authoring options warning keeps a server diagnostic id", async ({ page }) => {
  const definitionId = `diag-opt-${Date.now().toString(36)}`;
  await page.route("**/api/v2/admin/authoring-options", async (route) => {
    await route.fulfill({
      status: 500,
      contentType: "application/problem+json",
      body: JSON.stringify({
        title: "Internal Server Error",
        status: 500,
        detail: "The request could not be completed.",
        diagnosticId: "019944af-0008-7000-8000-0000000000e2"
      })
    });
  });
  await openNewDraftForm(page, definitionId);
  const form = page.locator("section[aria-label='Model and providers']");
  await expect(form.getByText("Authoring options could not be loaded.")).toBeVisible({ timeout: 15_000 });
  await form.getByRole("button", { name: "Error details" }).click();
  await expect(page.getByTestId("diagnostic-id")).toHaveText("019944af-0008-7000-8000-0000000000e2");
});

test("authoring options warning hides details when the response has no id", async ({ page }) => {
  const definitionId = `diag-plain-${Date.now().toString(36)}`;
  await page.route("**/api/v2/admin/authoring-options", async (route) => {
    await route.fulfill({
      status: 500,
      contentType: "application/problem+json",
      body: JSON.stringify({
        title: "Internal Server Error",
        status: 500,
        detail: "The request could not be completed."
      })
    });
  });
  await openNewDraftForm(page, definitionId);
  const form = page.locator("section[aria-label='Model and providers']");
  await expect(form.getByText("Authoring options could not be loaded.")).toBeVisible({ timeout: 15_000 });
  await expect(form.getByRole("button", { name: "Error details" })).toHaveCount(0);
});
