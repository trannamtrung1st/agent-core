import { expect, test, type Locator, type Page } from "@playwright/test";
import { forkBuiltInV1Draft, openDefinitionSettings } from "./admin-draft-editor-helpers";
import { completeDefinitionDraftPublishGate, publishDraftFromInstructions, isCanceledDraftEvidenceRead } from "./admin-definition-gate-helpers";

async function view(editor: Locator, name: "Form" | "Advanced JSON") {
  await editor.getByRole("radiogroup", { name: "Definition editor view" }).getByText(name, { exact: true }).click();
}
async function save(page: Page, editor: Locator, kinds: string[]) {
  const [response] = await Promise.all([
    page.waitForResponse(r => r.request().method() === "PUT" && r.url().includes("/definition-drafts/") && r.ok()),
    editor.getByRole("button", { name: "Save draft", exact: true }).click()
  ]);
  expect(response.request().postDataJSON().candidate.triggerPolicy.allowedSourceKinds).toEqual(kinds);
  await expect(page.getByText("Draft saved.", { exact: true })).toBeVisible();
  return response.url().split("/").at(-1)!;
}

for (const kind of ["coreEvent", "applicationEvent"]) {
  test(`${kind} stays restricted through editing, saving and responsive keyboard interaction`, async ({ page }) => {
    await page.goto("/admin/definitions/examiner/drafts");
    const editor = await forkBuiltInV1Draft(page);
    await openDefinitionSettings(editor, "Trigger restrictions");
    const label = kind === "coreEvent" ? "Built-in Events" : "Webhook Events";
    await editor.getByRole("checkbox", { name: label, exact: true }).check();
    await editor.getByRole("tab", { name: "Profile", exact: true }).click();
    await editor.getByLabel("Definition name", { exact: true }).fill(`Restricted ${kind}`);
    await openDefinitionSettings(editor, "Trigger restrictions");
    await view(editor, "Advanced JSON");
    const json = editor.getByRole("textbox", { name: "Advanced JSON", exact: true });
    const candidate = JSON.parse(await json.inputValue());
    expect(candidate.triggerPolicy.allowedSourceKinds).toEqual([kind]);
    candidate.triggerPolicy.allowedSourceKinds = ["schedule", kind];
    await json.fill(JSON.stringify(candidate, null, 2));
    await view(editor, "Form");
    const permissions = editor.getByRole("region", { name: "Automation trigger permissions", exact: true });
    const events = permissions.getByRole("checkbox", { name: "Events", exact: true });
    await expect(events).toHaveAttribute("aria-checked", "mixed");
    for (const width of [1440, 768, 390]) {
      await page.setViewportSize({ width, height: 900 });
      await events.scrollIntoViewIfNeeded();
      await events.focus();
      await expect(events).toBeFocused();
      await events.press("Space");
      await expect(events).not.toBeChecked();
      await expect(permissions.getByRole("checkbox", { name: "Schedule", exact: true })).toBeChecked();
      await events.press("Space");
      await expect(events).toHaveAttribute("aria-checked", "mixed");
      await events.press("Tab");
      await expect(permissions.getByRole("checkbox", { name: "Built-in Events", exact: true })).toBeFocused();
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
      for (const checkbox of await permissions.getByRole("checkbox").all()) {
        const box = await checkbox.boundingBox();
        expect(box).not.toBeNull();
        expect(box!.x).toBeGreaterThanOrEqual(0);
        expect(box!.x + box!.width).toBeLessThanOrEqual(width);
      }
    }
    const draftId = await save(page, editor, ["schedule", kind]);
    await page.reload();
    await page.locator(`[data-row-key="${draftId}"]`).getByRole("button", { name: /^Draft rev/ }).click();
    await openDefinitionSettings(editor);
    await expect(editor.getByLabel("System instructions")).toBeVisible();
    await view(editor, "Advanced JSON");
    expect(JSON.parse(await json.inputValue()).triggerPolicy.allowedSourceKinds).toEqual(["schedule", kind]);
  });
}

test("publish review and immutable details preserve independent Event permissions", async ({ page }) => {
  test.setTimeout(120_000);
  const errors: string[] = [];
  page.on("pageerror", error => errors.push(error.message));
  page.on("requestfailed", request => { if (!isCanceledDraftEvidenceRead(request)) errors.push(`${request.method()} ${request.url()}`); });
  await page.goto("/admin/definitions/examiner/drafts");
  const editor = await forkBuiltInV1Draft(page);
  await openDefinitionSettings(editor, "Trigger restrictions");
  await editor.getByRole("checkbox", { name: "Built-in Events", exact: true }).check();
  await save(page, editor, ["coreEvent"]);
  await completeDefinitionDraftPublishGate(page, editor);
  const gate = editor.getByLabel("Test validate and publish gate");
  await expect(gate.getByText(/^Automation trigger permissions \(Modified\)/)).toBeVisible();
  await expect(gate.getByText(/Built-in Events: Allowed; Webhook Events: Not allowed/)).toBeVisible();
  await publishDraftFromInstructions(page, editor);
  const version = (await page.getByText(/Published version \d+/).textContent())!.match(/Published version (\d+)/)![1];
  await page.getByRole("region", { name: "Definition drafts", exact: true }).getByRole("tab", { name: "Versions", exact: true }).click();
  await page.getByRole("button", { name: `View v${version} (durable)`, exact: true }).click();
  const details = page.getByRole("region", { name: "Version details", exact: true });
  await openDefinitionSettings(details, "Trigger restrictions");
  await expect(details.getByRole("checkbox", { name: "Built-in Events", exact: true })).toBeChecked();
  await expect(details.getByRole("checkbox", { name: "Built-in Events", exact: true })).toBeDisabled();
  await expect(details.getByRole("checkbox", { name: "Webhook Events", exact: true })).not.toBeChecked();
  await expect(details.getByRole("checkbox", { name: "Events", exact: true })).toHaveAttribute("aria-checked", "mixed");
  await expect(details.getByText(/do not authorize execution/)).toBeVisible();
  expect(errors).toEqual([]);
});
