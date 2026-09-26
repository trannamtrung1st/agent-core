import { expect, test } from "@playwright/test";
import {
  completeDefinitionDraftPublishGate,
  publishDraftFromInstructions
} from "./admin-definition-gate-helpers";
import { definitionDraftsSection, forkBuiltInV1Draft } from "./admin-draft-editor-helpers";

const antdNoise = (line: string) =>
  line.includes("[antd: List]") || line.includes("[antd: Alert]");

async function startSyntheticChat(page: import("@playwright/test").Page) {
  await page.goto("/");
  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Hello from synthetic.")).toBeVisible({ timeout: 15_000 });
  await page.waitForFunction(() => window.localStorage.getItem("agent-core.owner-capability"));
}

async function readWorkspaceText(page: import("@playwright/test").Page, sessionId: string, logicalPath: string) {
  return page.evaluate(
    async ({ sid, path }) => {
      const capability = window.localStorage.getItem("agent-core.owner-capability") ?? "";
      const response = await fetch(
        `/api/v2/sessions/${sid}/workspace/content?path=${encodeURIComponent(path)}`,
        { headers: { "X-AgentCore-Owner-Capability": capability } }
      );
      if (!response.ok) {
        return "";
      }

      const bytes = new Uint8Array(await response.arrayBuffer());
      return new TextDecoder().decode(bytes);
    },
    { sid: sessionId, path: logicalPath }
  );
}

test("admin resource publish managed chat exposes publication under agent", async ({ page }) => {
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

  const resourceBody = `e2e-resource-${Date.now()}`;
  const resourcePath = `knowledge/e2e-${Date.now()}.md`;
  const instructionMarker = `e2e-instruction-${Date.now()}`;

  await startSyntheticChat(page);
  await page.getByRole("button", { name: "Open Admin" }).click();
  await expect(page).toHaveURL(/\/admin$/);

  await page.locator('section[aria-label="Definitions"]').getByRole("button", { name: /examiner/i }).first().click();
  const draftEditor = await forkBuiltInV1Draft(page);

  await draftEditor.getByLabel("System instructions").fill(instructionMarker);

  await draftEditor.getByRole("tab", { name: "Capabilities" }).click();
  const harnessLabel = `e2e-harness-${Date.now()}`;
  await draftEditor.getByLabel("Harness labels").click();
  await draftEditor.getByLabel("Harness labels").fill(harnessLabel);
  await page.keyboard.press("Enter");
  await draftEditor.getByRole("button", { name: "Save draft" }).click();
  await expect(page.getByText("Draft saved.")).toBeVisible({ timeout: 15_000 });

  await draftEditor.getByRole("tab", { name: "Resources" }).click();
  await draftEditor.getByLabel("Resource logical path").fill(resourcePath);
  await draftEditor.getByLabel("Resource file").setInputFiles({
    name: "e2e.md",
    mimeType: "text/plain",
    buffer: Buffer.from(resourceBody, "utf8")
  });
  await draftEditor.getByRole("button", { name: "Upload and bind" }).click();
  await expect(draftEditor.getByText(resourcePath)).toBeVisible({ timeout: 15_000 });

  await completeDefinitionDraftPublishGate(page, draftEditor);
  await publishDraftFromInstructions(page, draftEditor);
  const publishedToast = page.getByText(/Published version \d+/);
  await expect(publishedToast).toBeVisible({ timeout: 15_000 });
  const version = (await publishedToast.textContent())?.match(/Published version (\d+)/)?.[1];
  expect(version).toBeTruthy();
  const publishedVersion = Number(version);

  const instanceResponsePromise = page.waitForResponse(
    (response) =>
      response.request().method() === "POST" &&
      response.url().includes("/api/v2/admin/agent-instances") &&
      response.ok()
  );
  const sessionResponsePromise = page.waitForResponse(
    (response) =>
      response.request().method() === "POST" &&
      /\/api\/v2\/sessions$/.test(response.url()) &&
      response.ok()
  );
  await definitionDraftsSection(page)
    .getByRole("button", { name: `Start managed chat for v${version}` })
    .click();
  const instanceResponse = await instanceResponsePromise;
  const sessionResponse = await sessionResponsePromise;
  const instance = (await instanceResponse.json()) as {
    instanceId: string;
    definitionId: string;
    activeVersion: number;
    compatibility: boolean;
  };
  const sessionRequest = sessionResponse.request().postDataJSON() as {
    agentInstanceId?: string;
    agentId?: string;
  };
  const sessionView = (await sessionResponse.json()) as {
    sessionId: string;
    agentId: string;
    agentVersion: number;
  };

  expect(instance.compatibility).toBe(false);
  expect(instance.definitionId).toBe("examiner");
  expect(instance.activeVersion).toBe(publishedVersion);
  expect(sessionRequest.agentInstanceId).toBe(instance.instanceId);
  expect(sessionRequest.agentId).toBeUndefined();
  expect(sessionView.agentId).toBe("examiner");
  expect(sessionView.agentVersion).toBe(publishedVersion);

  await expect(page).toHaveURL(/\/c\/[0-9a-f-]+/i, { timeout: 20_000 });
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 20_000 });
  expect(page.getByRole("button", { name: "Publish…" })).toHaveCount(0);

  const sessionId = sessionView.sessionId;
  const agentPath = `/agent/resources/${resourcePath}`;
  const content = await readWorkspaceText(page, sessionId, agentPath);
  expect(content).toContain(resourceBody);

  expect(failedRequests.filter((item) => !item.includes("favicon"))).toEqual([]);
  expect(consoleErrors.filter((line) => !antdNoise(line))).toEqual([]);
});
