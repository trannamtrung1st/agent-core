import { isCanceledDraftEvidenceRead } from "./admin-definition-gate-helpers";
import { startSyntheticChat } from "./admin-managed-helpers";
import { execFileSync } from "node:child_process";
import { expect, test, type Locator, type Page } from "@playwright/test";
import {
  completeDefinitionDraftPublishGate,
  ensureToolAllowlisted,
  publishDraftFromInstructions
} from "./admin-definition-gate-helpers";
import { definitionDraftsSection, draftEditorSection } from "./admin-draft-editor-helpers";

const marker = "[test:p85-journey] please review this account";
const intermediate = "Still checking the billing case.";
const answer = "Billing review is complete.";

const antdNoise = (line: string) =>
  line.includes("[antd: List]") || line.includes("[antd: Alert]") || line.includes("[antd: message]");

async function saveDraft(page: Page, editor: Locator) {
  await editor.getByRole("button", { name: "Save draft" }).click();
  await expect(page.getByText("Draft saved.")).toBeVisible({ timeout: 15_000 });
  await expect(page.getByText("Draft saved.")).toBeHidden({ timeout: 10_000 });
}

test("P8.5 sends one intermediate message, loads the missed skill, and keeps one answer", async ({ page }) => {
  test.setTimeout(300_000);
  await page.emulateMedia({ reducedMotion: "reduce" });
  await page.setViewportSize({ width: 1280, height: 900 });
  const consoleErrors: string[] = [];
  const failedRequests: string[] = [];
  page.on("console", (message) => {
    if (message.type() === "error") {
      consoleErrors.push(`${message.text()} [${message.location().url}]`);
    }
  });
  page.on("requestfailed", (request) => {
    if (!isCanceledDraftEvidenceRead(request)) failedRequests.push(`${request.method()} ${request.url()}`);
  });

  const definitionId = `p85-journey-${Date.now()}`;
  await startSyntheticChat(page);
  await page.getByRole("button", { name: "Open Admin" }).click();
  await expect(page).toHaveURL(/\/admin$/);
  await page.getByRole("button", { name: "New definition" }).click();
  const definitionDialog = page.getByRole("dialog", { name: "New definition" });
  await definitionDialog.getByLabel("Definition ID").fill(definitionId);
  await definitionDialog.getByRole("button", { name: "Create draft" }).click();
  await expect(page).toHaveURL(new RegExp(`/admin/definitions/${definitionId}$`));

  const editor = draftEditorSection(page);
  await editor.getByRole("button", { name: "Add skill" }).click();
  await page.getByRole("dialog", { name: "New Definition Skill" }).getByLabel("Skill ID", { exact: true }).fill("billing.review");
  await page.getByRole("dialog", { name: "New Definition Skill" }).getByLabel("Skill name", { exact: true }).fill("Billing review");
  await page.getByRole("dialog", { name: "New Definition Skill" }).getByLabel("Procedure", { exact: true }).fill("BILLING_PROCEDURE");
  await page.getByRole("dialog", { name: "New Definition Skill" }).getByLabel("Description", { exact: true }).fill("Procedural guidance.");
  await page.getByRole("dialog").getByRole("button", { name: "Save Skill" }).click();
  await expect(page.getByRole("dialog")).toBeHidden();
  await editor.getByRole("button", { name: "Add skill" }).click();
  await page.getByRole("dialog", { name: "New Definition Skill" }).getByLabel("Skill ID", { exact: true }).fill("order.lookup");
  await page.getByRole("dialog", { name: "New Definition Skill" }).getByLabel("Skill name", { exact: true }).fill("Order lookup");
  await page.getByRole("dialog", { name: "New Definition Skill" }).getByLabel("Procedure", { exact: true }).fill("ORDER_PROCEDURE");
  await page.getByRole("dialog", { name: "New Definition Skill" }).getByLabel("Description", { exact: true }).fill("Procedural guidance.");
  await page.getByRole("dialog").getByRole("button", { name: "Save Skill" }).click();
  await expect(page.getByRole("dialog")).toBeHidden();
  await saveDraft(page, editor);
  await ensureToolAllowlisted(page, editor, "workspace.list");
  await completeDefinitionDraftPublishGate(page, editor);
  await publishDraftFromInstructions(page, editor);

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

  await page.getByLabel("Message").fill(marker);
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText(intermediate)).toBeVisible({ timeout: 20_000 });
  await expect(page.getByText(answer)).toBeVisible({ timeout: 20_000 });
  await expect(page.getByText("Still working")).toBeVisible();
  await expect(page.locator('[data-role="applicationMessage"]')).toHaveCount(1);
  await expect(page.locator('[data-role="assistant"]')).toHaveCount(1);
  await expect(page.locator('[data-role="user"]')).toHaveCount(1);
  await expect(page.getByText("app.message.send")).toHaveCount(0);
  const roles = await page.locator(".conversation-list > li").evaluateAll((items) =>
    items.map((item) => item.getAttribute("data-role"))
  );
  expect(roles).toEqual(["user", "applicationMessage", "assistant"]);
  const finalTime = await page.locator('[data-role="assistant"] time').getAttribute("datetime");
  const progressTime = await page.locator('[data-role="applicationMessage"] time').getAttribute("datetime");
  expect(Date.parse(finalTime!)).toBeGreaterThanOrEqual(Date.parse(progressTime!));
  const history = await page.evaluate(async () => {
    const capability = await fetch("/api/v1/local/owner-capability", { method: "POST" }).then((response) => response.json());
    return fetch(`/api/v1/sessions/${location.pathname.split("/").at(-1)}/messages`, {
      headers: { "X-AgentCore-Owner-Capability": capability.token }
    }).then((response) => response.json());
  });
  const finalEntry = history.items.find((item: { role: string }) => item.role === "assistant");
  expect(finalTime).toBe(finalEntry.completedAt);

  await page.setViewportSize({ width: 390, height: 844 });
  await expect(page.getByRole("button", { name: "Open chats" })).toBeVisible();
  await expect(page.getByLabel("Message")).toBeVisible();
  await expect(page.getByText(intermediate)).toBeVisible();
  const overflow = await page.evaluate(() => {
    const message = document.querySelector('[data-role="applicationMessage"]');
    const column = document.querySelector(".conversation-list")?.closest(".conversation-column");
    return {
      message: message instanceof HTMLElement && message.scrollWidth > message.clientWidth + 1,
      column: column instanceof HTMLElement && column.scrollWidth > column.clientWidth + 1
    };
  });
  expect(overflow).toEqual({ message: false, column: false });
  await page.setViewportSize({ width: 1280, height: 900 });

  await page.reload();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 20_000 });
  await expect(page.getByText(intermediate)).toHaveCount(1);
  await expect(page.getByText("Still working")).toHaveCount(1);
  await expect(page.locator('[data-role="assistant"] time')).toHaveAttribute("datetime", finalTime!);
  await expect(page.getByText(answer)).toHaveCount(1);
  const motion = await page.locator('[data-role="applicationMessage"]').evaluate((element) => {
    const style = getComputedStyle(element);
    return { animationName: style.animationName, transitionDuration: style.transitionDuration };
  });
  expect(motion.animationName === "none" || motion.animationName === "").toBe(true);
  expect(motion.transitionDuration === "0s" || motion.transitionDuration === "").toBe(true);
  await expect(page.locator('[data-role="applicationMessage"]')).toHaveCount(1);
  await expect(page.locator('[data-role="assistant"]')).toHaveCount(1);
  await expect(page.locator('[data-role="user"]')).toHaveCount(1);

  const database = process.env.PLAYWRIGHT_SQLITE_PATH;
  expect(database).toBeTruthy();
  const pins = execFileSync("sqlite3", [database!, "SELECT json_extract(PayloadJson, '$.activeSkillKeys') FROM AgentRuns;"], {
    encoding: "utf8"
  });
  expect(pins).toContain("billing.review");
  expect(pins).not.toContain("order.lookup");

  await page.goto(`/admin/instances/${instance.instanceId}`);
  const lifecycle = page.getByRole("region", { name: "Lifecycle controls" });
  await lifecycle.getByRole("button", { name: "Archive instance" }).click();
  await page.getByRole("dialog").getByRole("button", { name: "Archive", exact: true }).click();
  await expect(page.getByText("Instance archived.")).toBeVisible({ timeout: 15_000 });

  expect(failedRequests.filter((item) => !item.includes("favicon"))).toEqual([]);
  expect(consoleErrors.filter((line) => !antdNoise(line) && !line.includes("favicon"))).toEqual([]);
});
