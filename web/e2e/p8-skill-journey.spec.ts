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

test("Definition authoring publishes Always and OnDemand Skills, initializes only Always, and keeps one chat response", async ({ page }) => {
  test.setTimeout(300_000);
  await page.setViewportSize({ width: 1280, height: 900 });
  const consoleErrors: string[] = [];
  const failedRequests: string[] = [];
  const failedResponses: string[] = [];
  page.on("console", (message) => {
    if (message.type() === "error") {
      consoleErrors.push(message.text());
    }
  });
  page.on("requestfailed", (request) => {
    failedRequests.push(`${request.method()} ${request.url()}`);
  });
  page.on("response", (response) => {
    if (response.status() >= 400) failedResponses.push(`${response.status()} ${response.url()}`);
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
  const drawer = page.getByRole("dialog", { name: "New Definition Skill" });
  await drawer.getByRole("button", { name: "Save Skill" }).click();
  await expect(drawer.getByText("Please enter Skill name")).toBeVisible();
  await expect(drawer.getByLabel("Skill ID", { exact: true })).toHaveValue("");
  await drawer.getByLabel("Skill ID", { exact: true }).fill("refund.handle");
  await drawer.getByLabel("Skill name", { exact: true }).fill("Refund");
  await drawer.getByLabel("Procedure", { exact: true }).fill("REFUND_PROCEDURE");
  await drawer.getByLabel("Description", { exact: true }).fill("Procedural guidance.");
  await drawer.getByLabel("Activation", { exact: true }).click();
  await page.locator(".ant-select-item-option").filter({ hasText: "Always" }).click();
  await drawer.getByLabel("Required capabilities", { exact: true }).fill("chat.respond");

  await drawer.getByLabel("Skill ID", { exact: true }).focus();
  await page.keyboard.press("Tab");
  await expect(drawer.getByLabel("Skill name", { exact: true })).toBeFocused();
  const focusShadow = await drawer.getByLabel("Skill name", { exact: true }).evaluate((element) => getComputedStyle(element).boxShadow);
  expect(focusShadow).not.toBe("none");

  await drawer.getByRole("button", { name: "Save Skill" }).click();
  await expect(drawer).toBeHidden();
  await editor.getByRole("button", { name: "Add skill" }).click();
  await drawer.getByLabel("Skill ID", { exact: true }).fill("order.lookup");
  await drawer.getByLabel("Skill name", { exact: true }).fill("Order lookup");
  await drawer.getByLabel("Procedure", { exact: true }).fill("ORDER_PROCEDURE");
  await drawer.getByLabel("Description", { exact: true }).fill("Procedural guidance.");

  await drawer.getByRole("button", { name: "Save Skill" }).click();
  await expect(drawer).toBeHidden();
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
  const skills = editor.getByRole("region", { name: "Skills", exact: true });
  await skills.getByRole("row").filter({ hasText: "Refund" }).getByRole("button", { name: "Details", exact: true }).click();
  await expect(page.getByRole("dialog")).toContainText("REFUND_PROCEDURE");
  await page.getByRole("button", { name: "Close inspection" }).click();
  await skills.getByRole("row").filter({ hasText: "Order lookup" }).getByRole("button", { name: "Details", exact: true }).click();
  await expect(page.getByRole("dialog")).toContainText("ORDER_PROCEDURE");
  await page.getByRole("button", { name: "Close inspection" }).click();
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
  const versionDetails = page.getByRole("region", { name: "Version details", exact: true });
  await expect(versionDetails.getByRole("region", { name: "Skills", exact: true })).toHaveCount(0);
  await versionDetails.getByRole("tab", { name: "Skills", exact: true }).click();
  const published = versionDetails.getByRole("region", { name: "Skills", exact: true });
  await expect(published).toBeVisible({ timeout: 15_000 });
  await expect(published).toContainText("Refund");
  await expect(published).toContainText("refund.handle");
  await expect(published).toContainText("Order lookup");
  await expect(published).toContainText("order.lookup");
  await expect(published).toContainText("Required capabilities are requirements, not grants.");
  await expect(published.getByRole("button", { name: "Add skill" })).toHaveCount(0);
  await published.getByRole("row").filter({ hasText: "Refund" }).getByRole("button", { name: "Details", exact: true }).click();
  const publishedSkill = page.getByRole("dialog", { name: "Definition Skill details", exact: true });
  await expect(publishedSkill).toContainText("REFUND_PROCEDURE");
  await expect(publishedSkill.getByRole("button", { name: "Save Skill" })).toHaveCount(0);
  await page.keyboard.press("Escape");
  await expect(publishedSkill).toBeHidden();
  await expect(page.getByRole("dialog", { name: "Version details", exact: true })).toBeVisible();
  await expect(published.getByRole("row").filter({ hasText: "Refund" }).getByRole("button", { name: "Details", exact: true })).toBeFocused();
  for (const width of [1440, 768, 390]) {
    await page.setViewportSize({ width, height: 900 });
    await expect(published.getByRole("row").filter({ hasText: "Refund" })).toContainText("chat.respond");
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
  }
  await page.setViewportSize({ width: 1280, height: 900 });
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
  const pins = execFileSync("sqlite3", [database!, "SELECT json_extract(PayloadJson, '$.activeSkillKeys') FROM AgentRuns;"], {
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
  expect(consoleErrors.filter((line) => !antdNoise(line) && !line.includes("favicon")), failedResponses.join("\n")).toEqual([]);
});
