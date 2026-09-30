import { execFileSync } from "node:child_process";
import { expect, test, type Locator, type Page } from "@playwright/test";
import {
  completeDefinitionDraftPublishGate,
  publishDraftFromInstructions
} from "./admin-definition-gate-helpers";
import { definitionDraftsSection, draftEditorSection } from "./admin-draft-editor-helpers";

const marker = "[test:p85-journey] please review this account";
const intermediate = "Still checking the billing case.";
const answer = "Billing review is complete.";

const antdNoise = (line: string) =>
  line.includes("[antd: List]") || line.includes("[antd: Alert]") || line.includes("[antd: message]");

async function startSyntheticChat(page: Page) {
  await page.goto("/");
  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Hello from synthetic.")).toBeVisible({ timeout: 15_000 });
  await page.waitForFunction(() => window.localStorage.getItem("agent-core.owner-capability"));
}

async function saveDraft(page: Page, editor: Locator) {
  await editor.getByRole("button", { name: "Save draft" }).click();
  await expect(page.getByText("Draft saved.")).toBeVisible({ timeout: 15_000 });
  await expect(page.getByText("Draft saved.")).toBeHidden({ timeout: 10_000 });
}

test("P8.5 sends one intermediate message, loads the missed skill, and keeps one answer", async ({ page }) => {
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
  await editor.getByLabel("Skill 1 id").fill("billing.review");
  await editor.getByLabel("Skill 1 name").fill("Billing review");
  await editor.getByLabel("Skill 1 procedure").fill("BILLING_PROCEDURE");
  await editor.getByLabel("Skill 1 activation keywords").fill("invoice");
  await editor.getByRole("button", { name: "Add skill" }).click();
  await editor.getByLabel("Skill 2 id").fill("order.lookup");
  await editor.getByLabel("Skill 2 name").fill("Order lookup");
  await editor.getByLabel("Skill 2 procedure").fill("ORDER_PROCEDURE");
  await editor.getByLabel("Skill 2 activation keywords").fill("order");
  await saveDraft(page, editor);
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
  await expect(page.locator('[data-role="applicationMessage"]')).toHaveCount(1);
  await expect(page.locator('[data-role="assistant"]')).toHaveCount(1);
  await expect(page.locator('[data-role="user"]')).toHaveCount(1);

  await page.setViewportSize({ width: 390, height: 844 });
  await expect(page.getByLabel("Message")).toBeVisible();
  await expect(page.getByText(intermediate)).toBeVisible();
  await expect(page.getByText(answer)).toBeVisible();
  await page.setViewportSize({ width: 1280, height: 900 });

  await page.reload();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 20_000 });
  await expect(page.getByText(intermediate)).toHaveCount(1);
  await expect(page.getByText(answer)).toHaveCount(1);
  await expect(page.locator('[data-role="applicationMessage"]')).toHaveCount(1);
  await expect(page.locator('[data-role="assistant"]')).toHaveCount(1);
  await expect(page.locator('[data-role="user"]')).toHaveCount(1);

  const database = process.env.PLAYWRIGHT_SQLITE_PATH;
  expect(database).toBeTruthy();
  const pins = execFileSync("sqlite3", [database!, "SELECT PinnedActiveSkillIdsJson FROM ConversationTurnExecutions;"], {
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
