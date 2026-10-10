import { INSTANCE_DEFINITIONS, selectInstanceIdentity } from "./support/instance-identity";
import { expect, test } from "@playwright/test";

test("over-limit input remains recoverable without submitting", async ({ page }) => {
  await page.goto("/");
  await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.examiner);
  const editor = page.getByLabel("Message", { exact: true });
  await editor.fill("x".repeat(8001));
  await expect(page.getByRole("button", { name: "Send", exact: true })).toBeDisabled();
  await expect(page.getByRole("alert")).toContainText("8000 characters");
  await expect(page.locator(".chat-message-user")).toHaveCount(0);
  await editor.fill("Recovered draft");
  await expect(page.getByRole("alert")).toHaveCount(0);
  await page.getByRole("button", { name: "Send", exact: true }).click();
  await expect(page.locator(".chat-message-assistant")).toContainText("Hello from synthetic.");
});

test("a stale Skill selection keeps structured recoverable server details", async ({ page }) => {
  await page.goto("/");
  await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.examiner);
  const editor = page.getByLabel("Message", { exact: true });
  await editor.fill("Hello");
  await page.getByRole("button", { name: "Send", exact: true }).click();
  await expect(page.locator(".chat-message-assistant")).toContainText("Hello from synthetic.");
  const sessionId = page.url().split("/c/")[1];
  const headers = { "X-AgentCore-Owner-Capability": (await page.evaluate(() => localStorage.getItem("agent-core.owner-capability")))! };
  const session = await page.request.get(`/api/v1/sessions/${sessionId}`, { headers });
  const instanceId = (await session.json()).agentInstanceId;
  const created = await page.request.post(`/api/v2/admin/agent-instances/${instanceId}/skills`, {
    headers,
    data: { id: "validation", name: "Validation", description: "Validate task", procedure: "Validate the task", projection: "OnDemand", enabled: true, requiredCapabilities: [] },
  });
  expect(created.ok(), await created.text()).toBe(true);
  const skill = await created.json();
  await editor.pressSequentially("/validation");
  await page.getByRole("option").filter({ hasText: "instance:validation" }).click();
  await editor.pressSequentially("Inspect this");
  const disabled = await page.request.put(`/api/v2/admin/agent-instances/${instanceId}/skills/${encodeURIComponent(skill.key)}/enabled`, {
    headers, data: { expectedRevision: skill.revision, enabled: false },
  });
  expect(disabled.ok(), await disabled.text()).toBe(true);
  await page.getByRole("button", { name: "Send", exact: true }).click();
  const alert = page.getByTestId("session-failure");
  await expect(alert).toHaveAttribute("data-error-class", "validation/protocol");
  await expect(alert).toHaveAttribute("data-error-code", "ValidationError");
  await expect(alert).toHaveAttribute("data-error-fatal", "false");
  await expect(alert).toContainText("Selected Skill instance:validation is unavailable");
  await page.getByRole("button", { name: "Failure details" }).click();
  await expect(page.getByTestId("session-failure-details")).toContainText("Recoverable");
  await expect(editor.locator('[data-skill-key="instance:validation"]')).toHaveCount(1);
  await expect(page.locator(".chat-message-user")).toHaveCount(1);
});

test("rich envelope shows differing spoken text without another message and does not replay thinking after reload", async ({ page }) => {
  await page.goto("/");
  await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.examiner);
  await page.getByLabel("Message").fill("[test:rich-envelope]");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Shown display.")).toBeVisible({ timeout: 15_000 });
  await expect(page.getByText("Extra block")).toBeVisible();
  await expect(page.getByText("fixture-attachment-1")).toBeVisible();
  await expect(page.getByText("File unavailable")).toBeVisible();
  await expect(page.getByText("fixture-artifact-1")).toHaveCount(0);
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
