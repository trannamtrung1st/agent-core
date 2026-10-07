import { startSyntheticChat } from "./admin-managed-helpers";
import { execFileSync } from "node:child_process";
import { expect, test, type Locator, type Page } from "@playwright/test";
import {
  completeDefinitionDraftPublishGate,
  publishDraftFromInstructions
} from "./admin-definition-gate-helpers";
import { definitionDraftsSection, draftEditorSection } from "./admin-draft-editor-helpers";

const antdNoise = (line: string) =>
  line.includes("[antd: List]") || line.includes("[antd: Alert]") || line.includes("[antd: message]");

async function saveDraft(page: Page, editor: Locator) {
  await editor.getByRole("button", { name: "Save draft" }).click();
  await expect(page.getByText("Draft saved.")).toBeVisible({ timeout: 15_000 });
  await expect(page.getByText("Draft saved.")).toBeHidden({ timeout: 10_000 });
}

test("P8 publishes two skills, activates the matching one, and keeps one chat response", async ({ page }) => {
  test.setTimeout(300_000);
  await page.setViewportSize({ width: 1280, height: 900 });
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

  const definitionId = `p8-skills-${Date.now()}`;
  await startSyntheticChat(page);
  await page.getByRole("button", { name: "Open Admin" }).click();
  await expect(page).toHaveURL(/\/admin$/);
  await page.getByRole("button", { name: "New definition" }).click();
  const definitionDialog = page.getByRole("dialog", { name: "New definition" });
  await definitionDialog.getByLabel("Definition ID").fill(definitionId);
  await definitionDialog.getByRole("button", { name: "Create draft" }).click();
  await expect(page).toHaveURL(new RegExp(`/admin/definitions/${definitionId}$`));

  const editor = draftEditorSection(page);
  await expect(editor.getByLabel("System instructions")).toBeVisible({ timeout: 15_000 });
  await expect(editor.getByText("No skills yet. A definition without skills runs with an empty skill set.")).toBeVisible();
  await expect(editor.getByText(/Required capabilities are requirements, not grants/)).toBeVisible();

  await editor.getByRole("button", { name: "Add skill" }).click();
  await expect(editor.getByText("Skill id is required before publish.")).toBeVisible();
  await editor.getByLabel("Skill 1 id").fill("refund.handle");
  await editor.getByLabel("Skill 1 name").fill("Refund");
  await editor.getByLabel("Skill 1 procedure").fill("REFUND_PROCEDURE");
  await editor.getByLabel("Skill 1 activation keywords").fill("refund");
  await editor.getByLabel("Skill 1 required capabilities").fill("chat.respond");
  await expect(editor.getByText("Skill id is required before publish.")).toHaveCount(0);
  await expect(editor.getByText("Unsaved changes — save before using Test & Publish.")).toBeVisible();
  await expect(editor.getByRole("button", { name: "Publish…" })).toBeDisabled();

  await editor.getByLabel("Skill 1 id").focus();
  await page.keyboard.press("Tab");
  await expect(editor.getByLabel("Skill 1 name")).toBeFocused();
  const focusShadow = await editor.getByLabel("Skill 1 name").evaluate((element) => getComputedStyle(element).boxShadow);
  expect(focusShadow).not.toBe("none");

  await editor.getByRole("button", { name: "Add skill" }).click();
  await editor.getByLabel("Skill 2 id").fill("order.lookup");
  await editor.getByLabel("Skill 2 name").fill("Order lookup");
  await editor.getByLabel("Skill 2 procedure").fill("ORDER_PROCEDURE");
  await editor.getByLabel("Skill 2 activation keywords").fill("order");

  await editor.locator(".admin-draft-view-switch").getByText("Advanced JSON", { exact: true }).click();
  const json = editor.getByRole("textbox", { name: "Advanced JSON" });
  await expect(json).toHaveValue(/REFUND_PROCEDURE/);
  await expect(json).toHaveValue(/ORDER_PROCEDURE/);
  const validJson = await json.inputValue();
  await json.fill("{");
  await expect(editor.getByRole("alert").filter({ hasText: "Advanced JSON is invalid" })).toBeVisible();
  await expect(editor.getByRole("button", { name: "Publish…" })).toBeDisabled();
  await json.fill(validJson);
  await expect(editor.getByRole("alert").filter({ hasText: "Advanced JSON is invalid" })).toHaveCount(0);
  await editor.locator(".admin-draft-view-switch").getByText("Form", { exact: true }).click();
  await expect(editor.getByLabel("Skill 1 procedure")).toHaveValue("REFUND_PROCEDURE");
  await expect(editor.getByLabel("Skill 2 procedure")).toHaveValue("ORDER_PROCEDURE");
  await saveDraft(page, editor);

  await page.setViewportSize({ width: 390, height: 844 });
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1);
  expect(overflow).toBe(false);
  await page.setViewportSize({ width: 1280, height: 900 });

  let releaseEvidence = () => {};
  const evidenceHeld = new Promise<void>((resolve) => {
    releaseEvidence = resolve;
  });
  const holdEvidence = async (route: { continue: () => Promise<void> }) => {
    await evidenceHeld;
    await route.continue();
  };
  await page.route("**/evaluation-scenarios**", holdEvidence);
  await page.route("**/evaluation-results**", holdEvidence);
  await editor.getByRole("tab", { name: "Test & Publish" }).click();
  const gate = editor.getByLabel("Test validate and publish gate");
  await expect(gate.getByRole("alert").filter({ hasText: "Publish blocked" })).toBeVisible();
  await expect(gate.getByText("Evaluation evidence is not ready for the active draft.")).toBeVisible();
  await expect(gate.locator(".ant-spin")).toBeVisible();
  releaseEvidence();
  await expect(gate.locator(".ant-spin")).toHaveCount(0, { timeout: 15_000 });

  await completeDefinitionDraftPublishGate(page, editor);
  await publishDraftFromInstructions(page, editor);

  await page.getByRole("button", { name: "View v1 (durable)", exact: true }).click();
  const published = page.getByRole("region", { name: "Version details", exact: true }).getByRole("list", { name: "Published skills" });
  await expect(published).toBeVisible({ timeout: 15_000 });
  await expect(published).toContainText("Refund (refund.handle)");
  await expect(published).toContainText("Order lookup (order.lookup)");
  await expect(published).toContainText("Requirements do not grant tools, credentials, or approval.");
  await expect(published.getByRole("textbox")).toHaveCount(0);
  await page.getByRole("dialog", { name: "Version details", exact: true }).getByRole("button", { name: "Close", exact: true }).click();

  const instanceResponsePromise = page.waitForResponse(
    (response) =>
      response.request().method() === "POST"
      && response.url().includes("/api/v2/admin/agent-instances")
      && response.ok()
  );
  await definitionDraftsSection(page).getByRole("button", { name: "Start managed chat for v1" }).click();
  const instance = (await (await instanceResponsePromise).json()) as { instanceId: string };
  expect(instance.instanceId).toBeTruthy();
  await expect(page).toHaveURL(/\/c\/[0-9a-f-]+/i, { timeout: 20_000 });
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 20_000 });
  await page.getByLabel("Message").fill("please refund this");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Hello from synthetic.")).toHaveCount(1, { timeout: 20_000 });
  await expect(page.locator('[data-role="assistant"]')).toHaveCount(1);

  await page.reload();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 20_000 });
  await expect(page.getByText("Hello from synthetic.")).toHaveCount(1);
  await expect(page.locator('[data-role="assistant"]')).toHaveCount(1);

  const database = process.env.PLAYWRIGHT_SQLITE_PATH;
  expect(database).toBeTruthy();
  const pins = execFileSync("sqlite3", [database!, "SELECT PinnedActiveSkillIdsJson FROM ConversationTurnExecutions;"], {
    encoding: "utf8"
  });
  expect(pins).toContain("refund.handle");
  expect(pins).not.toContain("order.lookup");

  await page.goto(`/admin/instances/${instance.instanceId}`);
  const lifecycle = page.getByRole("region", { name: "Lifecycle controls" });
  await lifecycle.getByRole("button", { name: "Archive instance" }).click();
  await page.getByRole("dialog").getByRole("button", { name: "Archive", exact: true }).click();
  await expect(page.getByText("Instance archived.")).toBeVisible({ timeout: 15_000 });

  expect(failedRequests.filter((item) => !item.includes("favicon"))).toEqual([]);
  expect(consoleErrors.filter((line) => !antdNoise(line) && !line.includes("favicon"))).toEqual([]);
});
