import { navigateAdmin, backToInventory } from "./support/admin-navigation";
import { isCanceledEmailAccountPoll } from './support/request-failures';
import { openDefinitionSettings } from "./admin-draft-editor-helpers";
import { isCanceledDraftEvidenceRead } from "./admin-definition-gate-helpers";
import { startSyntheticChat } from "./admin-managed-helpers";
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

async function showAdvancedJson(editor: Locator) {
  await navigateAdmin(editor, "Identity & version");
  await navigateAdmin(editor, "Advanced JSON");
}

async function selectOption(page: Page, combobox: Locator, optionText: string) {
  await combobox.click();
  if (await combobox.getAttribute("aria-label") === "Definition") await combobox.fill(optionText);
  const option = page.locator(".ant-select-item-option").filter({ hasText: optionText }).last();
  await expect(option).toBeVisible({ timeout: 15_000 });
  await option.click();
}

test("P7.6 admin journey publishes a new definition and opens managed chat", async ({ page }) => {
  test.setTimeout(300_000);
  const consoleErrors: string[] = [];
  const failedRequests: string[] = [];
  page.on("console", (message) => {
    if (message.type() === "error") {
      consoleErrors.push(message.text());
    }
  });
  page.on("requestfailed", (request) => {
    if (isCanceledEmailAccountPoll(request)) return;
    if (!isCanceledDraftEvidenceRead(request)) failedRequests.push(`${request.method()} ${request.url()}`);
  });

  const definitionId = `p76-${Date.now()}`;
  const instructions = `Follow the P7.6 operator guide ${definitionId}.`;
  const personaName = "P76 Guide";

  await startSyntheticChat(page);
  await page.getByRole("link", { name: "Open Admin" }).click();
  await expect(page).toHaveURL(/\/admin\/instances$/);
  await navigateAdmin(page, "Definitions");
  await expect(page.getByRole("heading", { name: "Definitions", level: 2 })).toBeVisible();

  await page.getByRole("button", { name: "New definition" }).click();
  const definitionDialog = page.getByRole("dialog", { name: "New definition" });
  await definitionDialog.getByLabel("Definition ID").fill(definitionId);
  await definitionDialog.getByRole("button", { name: "Create draft" }).click();
  await expect(page).toHaveURL(new RegExp(`/admin/definitions/${definitionId}/drafts/[0-9a-f-]+/overview$`));

  const editor = draftEditorSection(page);
  await openDefinitionSettings(editor);
  await expect(editor.getByLabel("Agent instructions")).toBeVisible({ timeout: 15_000 });
  await editor.getByLabel("Agent instructions").fill(instructions);
  await saveDraft(page, editor);

  await showAdvancedJson(editor);
  const json = editor.getByRole("textbox", { name: "Advanced JSON" });
  await expect(json).toHaveValue(new RegExp(instructions.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
  const withTone = (await json.inputValue()).replace("\"tone\": \"Clear\"", "\"tone\": \"Warm\"");
  expect(withTone).toContain("\"tone\": \"Warm\"");
  await json.fill(withTone);
  await saveDraft(page, editor);

  await navigateAdmin(editor, "Skills & resources");
  await navigateAdmin(editor, "Resources");
  const filePicker = editor.locator('section[aria-label="Import resources"] input[type="file"]').first();
  await filePicker.setInputFiles([
    { name: "policy.md", mimeType: "text/markdown", buffer: Buffer.from("# Refund policy\n", "utf8") },
    { name: "notes.txt", mimeType: "text/plain", buffer: Buffer.from("operator notes\n", "utf8") }
  ]);
  await editor.getByLabel("Imported resource path 1").fill("knowledge/policy.md");
  await editor.getByLabel("Imported resource path 2").fill("references/notes.txt");
  const preview = editor.locator(".admin-resource-preview");
  await expect(preview).toContainText("Knowledge");
  await expect(preview).toContainText("Reference");
  await expect(preview).toContainText("Ready");
  await editor.getByRole("button", { name: "Bind imported resources" }).click();
  await expect(page.getByText("Resources bound.")).toBeVisible({ timeout: 20_000 });
  await expect(editor.getByText("knowledge/policy.md")).toBeVisible();
  await expect(editor.getByText("references/notes.txt")).toBeVisible();

  await filePicker.setInputFiles({
    name: "oversized.bin",
    mimeType: "application/octet-stream",
    buffer: Buffer.alloc(8 * 1024 * 1024 + 1)
  });
  await expect(editor.getByText("File is larger than 8 MiB.")).toBeVisible();
  await expect(editor.getByRole("button", { name: "Bind imported resources" })).toBeDisabled();
  await navigateAdmin(editor, "Test & Publish");
  await expect(editor.getByRole("button", { name: "Save required scenario" })).toBeDisabled();
  await editor.getByRole("link", { name: /Finish or discard unsaved resource input/ }).click();
  await expect(editor.getByText("File is larger than 8 MiB.")).toBeVisible();
  await editor.getByRole("button", { name: "Remove imported resource 1", exact: true }).click();
  await expect(editor.getByText("File is larger than 8 MiB.")).toHaveCount(0);
  await expect(editor.getByText("knowledge/policy.md")).toBeVisible();
  await expect(editor.getByText("references/notes.txt")).toBeVisible();

  await navigateAdmin(editor, "Capabilities");
  await editor.getByRole("button", { name: "Add knowledge source" }).click();
  await editor.getByLabel("Knowledge identity 1").fill("refund-policy");
  await editor.getByLabel("Knowledge title 1").fill("Refund policy");
  await editor.getByLabel("Knowledge citation 1").fill("policy@p76");
  await selectOption(page, editor.getByLabel("Knowledge resource 1"), "knowledge/policy.md");
  await saveDraft(page, editor);

  await navigateAdmin(editor, "Identity & version");
  await showAdvancedJson(editor);
  const publishedJson = editor.getByRole("textbox", { name: "Advanced JSON" });
  const validJson = await publishedJson.inputValue();
  expect(validJson).toContain("\"tone\": \"Warm\"");
  expect(validJson).toContain("knowledge/policy.md");
  await publishedJson.fill("{");
  await expect(editor.getByRole("alert").filter({ hasText: "Advanced JSON is invalid" })).toBeVisible();
  await expect(editor.getByRole("button", { name: "Publish…" })).toBeDisabled();
  await expect(page.getByText(/Published version \d+/)).toHaveCount(0);
  await publishedJson.fill(validJson);
  await expect(editor.getByRole("alert").filter({ hasText: "Advanced JSON is invalid" })).toHaveCount(0);
  if (await editor.getByRole("button", { name: "Save draft" }).isEnabled()) {
    await saveDraft(page, editor);
  }

  await completeDefinitionDraftPublishGate(page, editor);
  await publishDraftFromInstructions(page, editor);

  await backToInventory(page);
  await expect(page).toHaveURL(/\/admin\/definitions$/);
  await navigateAdmin(page, 'Instances');
  await page.getByRole("button", { name: "New instance" }).click();
  const instanceDialog = page.getByRole("dialog", { name: "New instance" });
  await selectOption(page, instanceDialog.getByRole("combobox", { name: "Definition", exact: true }), definitionId);
  await instanceDialog.getByRole("radio", { name: "Custom persona" }).click();
  await instanceDialog.getByLabel("Persona name").fill(personaName);
  await instanceDialog.getByLabel("Persona role").fill("Guide");
  await instanceDialog.getByLabel("Persona description").fill("Helps the operator through the P7.6 admin journey.");
  await instanceDialog.getByLabel("Persona tone").fill("Warm");
  await instanceDialog.getByRole("button", { name: "Create instance" }).click();
  await expect(page).toHaveURL(/\/admin\/instances\/[0-9a-f-]+\/overview$/i);
  await expect(page.getByRole("heading", { name: personaName })).toBeVisible({ timeout: 15_000 });
  await expect(page.getByText(`${definitionId} · v1`)).toBeVisible();
  await navigateAdmin(page, "Profile");
  await navigateAdmin(page, "Definition version");
  await expect(page.getByRole("region", { name: "Active version" })).toBeVisible();
  await expect(page.getByLabel("Target definition version")).toBeVisible();

  const lifecycle = page.getByRole("region", { name: "Lifecycle controls" });
  await navigateAdmin(page, "Lifecycle");
  await expect(lifecycle.getByText("Active", { exact: true })).toBeVisible();
  await lifecycle.getByRole("button", { name: "Archive instance" }).click();
  await page.getByRole("dialog").getByRole("button", { name: "Archive", exact: true }).click();
  await expect(page.getByText("Instance archived.")).toBeVisible({ timeout: 15_000 });
  await expect(lifecycle.getByText("Archived", { exact: true })).toBeVisible();
  await lifecycle.getByRole("button", { name: "Unarchive instance" }).click();
  await expect(page.getByText("Instance unarchived.")).toBeVisible({ timeout: 15_000 });
  await expect(lifecycle.getByText("Active", { exact: true })).toBeVisible();

  await navigateAdmin(page, "Continuity");
  const memory = page.locator(".admin-content");
  await navigateAdmin(memory, "Memory");
  await expect(memory.getByText("Effective memory policy", { exact: true })).toBeVisible();
  await navigateAdmin(page, "Automation");
  await navigateAdmin(page, "Policies & models");
  await expect(page.getByLabel("Automation administration")).toBeVisible();

  await navigateAdmin(page, "Effective configuration");
  const effective = page.getByRole("region", { name: "Effective configuration" });
  await expect(effective.getByText("Catalog key")).toBeVisible();
  await expect(effective.getByRole("button", { name: "Save screenshot privacy", exact: true })).toHaveCount(0);
  await effective.getByRole("link", { name: /Manage global Browser privacy/ }).click();
  const privacy = page.getByRole("region", { name: "Screenshot privacy", exact: true });
  await expect(privacy.getByRole("button", { name: "Save screenshot privacy", exact: true })).toBeVisible();
  await expect(privacy.getByRole("combobox", { name: /Saved screenshot privacy mode/ })).toBeVisible();
  for (const section of await effective.locator('.admin-effective-config-section:not([aria-label="Browser provider"])').all()) {
    for (const role of ["textbox", "button", "combobox", "switch"] as const) {
      await expect(section.getByRole(role)).toHaveCount(0);
    }
  }

  await page.goto("/admin/definitions");
  const definitions = page.getByRole("region", { name: "Definitions", exact: true });
  await navigateAdmin(page, "Definitions");
  await definitions.getByLabel("Search definitions", { exact: true }).fill(definitionId);
  await definitions.getByRole("link", { name: new RegExp(definitionId) }).click();
  await expect(definitionDraftsSection(page).getByRole("button", { name: "Start managed chat for v1" })).toBeEnabled();
  await definitionDraftsSection(page).getByRole("button", { name: "Start managed chat for v1" }).click();
  await expect(page).toHaveURL(/\/c\/[0-9a-f-]+/i, { timeout: 20_000 });
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 20_000 });
  await page.getByLabel("Message").fill("Hello from the published guide");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Hello from synthetic.")).toBeVisible({ timeout: 15_000 });

  expect(failedRequests.filter((item) => !item.includes("favicon"))).toEqual([]);
  expect(consoleErrors.filter((line) => !antdNoise(line))).toEqual([]);
});
