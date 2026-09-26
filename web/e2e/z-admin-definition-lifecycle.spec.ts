import { expect, test } from "@playwright/test";
import {
  completeDefinitionDraftPublishGate,
  publishDraftFromInstructions
} from "./admin-definition-gate-helpers";
import {
  definitionDraftsSection,
  forkBuiltInV1Draft,
  forkDurablePublicationDraft
} from "./admin-draft-editor-helpers";

const antdNoise = (line: string) =>
  line.includes("[antd: List]") || line.includes("[antd: Alert]");

async function startSyntheticChat(page: import("@playwright/test").Page) {
  await page.goto("/");
  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Hello from synthetic.")).toBeVisible({ timeout: 15_000 });
  await page.waitForFunction(() => window.localStorage.getItem("agent-core.owner-capability"));
}

test("admin definition fork edit and publish durable version", async ({ page }) => {
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

  const marker = `e2e-p7b-marker-${Date.now()}`;
  await startSyntheticChat(page);

  await page.getByRole("button", { name: "Open Admin" }).click();
  await expect(page).toHaveURL(/\/admin$/);

  const definitions = page.locator('section[aria-label="Definitions"]');
  await definitions.getByRole("button", { name: /examiner/i }).first().click();
  await expect(page).toHaveURL(/\/admin\/definitions\/examiner$/i);

  const draftEditor = await forkBuiltInV1Draft(page);

  const instructions = draftEditor.getByLabel("System instructions");
  const prior = (await instructions.inputValue()) || "";
  await instructions.fill(`${prior}\n${marker}`);

  await expect(
    draftEditor.getByText("Unsaved changes — save before using Test & Publish.")
  ).toBeVisible();

  await draftEditor.getByRole("button", { name: "Save draft" }).click();
  await expect(page.getByText("Draft saved.")).toBeVisible({ timeout: 15_000 });

  await completeDefinitionDraftPublishGate(page, draftEditor);
  await publishDraftFromInstructions(page, draftEditor);

  const publishedToast = page.getByText(/Published version \d+/);
  await expect(publishedToast).toBeVisible({ timeout: 15_000 });
  const version = (await publishedToast.textContent())?.match(/Published version (\d+)/)?.[1];
  expect(version).toBeTruthy();

  const draftsList = definitionDraftsSection(page);
  await expect(draftsList.getByText("Durable publications")).toBeVisible({ timeout: 15_000 });
  const forkedEditor = await forkDurablePublicationDraft(page, Number(version));
  await expect(forkedEditor.getByLabel("System instructions")).toHaveValue(new RegExp(marker), {
    timeout: 15_000
  });

  expect(failedRequests.filter((item) => !item.includes("favicon"))).toEqual([]);
  expect(consoleErrors.filter((line) => !antdNoise(line))).toEqual([]);
  expect(consoleErrors.some((line) => line.includes("Static function can not consume context"))).toBe(false);
});
