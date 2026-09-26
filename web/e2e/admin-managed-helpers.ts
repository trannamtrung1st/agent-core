import { expect, type Page } from "@playwright/test";
import {
  completeDefinitionDraftPublishGate,
  ensureToolAllowlisted,
  publishDraftFromInstructions
} from "./admin-definition-gate-helpers";

export type SessionView = {
  sessionId: string;
  agentId: string;
  agentVersion: number;
  agentInstanceId: string | null;
  pinnedPersonaRevision: number | null;
};

export type ManagedInstance = {
  instanceId: string;
  definitionId: string;
  activeVersion: number;
  compatibility: boolean;
  personaRevision: number;
};

async function patchExaminerDraftMemoryPolicy(page: Page, draftId: string) {
  await page.evaluate(async (draftId) => {
    const token = window.localStorage.getItem("agent-core.owner-capability") ?? "";
    const headers = {
      "content-type": "application/json",
      "X-AgentCore-Owner-Capability": token
    };
    const current = await fetch(`/api/v2/admin/definition-drafts/${draftId}`, { headers });
    if (!current.ok) {
      throw new Error(`load draft ${current.status}`);
    }
    const draft = (await current.json()) as { candidate: Record<string, unknown>; revision: number };
    const candidate = draft.candidate;
    candidate.memoryPolicy = {
      sessionMemory: true,
      identityUserPromotion: true,
      identityUserRetrieval: true,
      userPromotion: false,
      userRetrieval: false
    };
    const put = await fetch(`/api/v2/admin/definition-drafts/${draftId}`, {
      method: "PUT",
      headers,
      body: JSON.stringify({ expectedRevision: draft.revision, candidate })
    });
    if (!put.ok) {
      throw new Error(await put.text());
    }
  }, draftId);
}

async function reopenExaminerDraftByMarker(page: Page, marker: string) {
  const draftsSection = page.locator('section[aria-label="Definition drafts"]');
  await draftsSection.getByRole("button", { name: /Draft rev .*ForkBuiltIn/ }).first().click();
  await expect(draftsSection.getByLabel("System instructions")).toHaveValue(new RegExp(marker), {
    timeout: 15_000
  });
  return draftsSection;
}

/** P7G §8 steps 3–11: fork, resource, capability, memory policy, publish gate, managed instance. */
export async function publishExaminerP7gFirstPublication(page: Page) {
  await page.getByRole("button", { name: "Open Admin" }).click();
  await expect(page).toHaveURL(/\/admin$/);
  await page.locator('section[aria-label="Definitions"]').getByRole("button", { name: /examiner/i }).first().click();

  const draftsSection = page.locator('section[aria-label="Definition drafts"]');
  const forkResponsePromise = page.waitForResponse(
    (response) =>
      response.request().method() === "POST" &&
      response.url().includes("/api/v2/admin/definition-drafts/fork") &&
      response.ok()
  );
  await page.getByRole("button", { name: /Fork v1 \(builtIn\)/ }).click();
  const forkResponse = await forkResponsePromise;
  const forkBody = (await forkResponse.json()) as { draftId: string };

  await expect(draftsSection.getByLabel("System instructions")).toBeVisible({ timeout: 15_000 });
  const marker = `p7g-whole-${Date.now()}`;
  await draftsSection.getByLabel("System instructions").fill(marker);

  await draftsSection.getByRole("tab", { name: "Capabilities" }).click();
  const harnessLabel = `p7g-harness-${Date.now()}`;
  await draftsSection.getByLabel("Harness labels").click();
  await draftsSection.getByLabel("Harness labels").fill(harnessLabel);
  await page.keyboard.press("Enter");
  await ensureToolAllowlisted(page, draftsSection, "knowledge.retrieve");

  const resourceBody = `p7g-resource-${Date.now()}`;
  const resourcePath = `knowledge/p7g-${Date.now()}.md`;
  await draftsSection.getByRole("tab", { name: "Resources" }).click();
  await draftsSection.getByLabel("Resource logical path").fill(resourcePath);
  await draftsSection.getByLabel("Resource file").setInputFiles({
    name: "p7g.md",
    mimeType: "text/plain",
    buffer: Buffer.from(resourceBody, "utf8")
  });
  await draftsSection.getByRole("button", { name: "Upload and bind" }).click();
  await expect(draftsSection.getByText(resourcePath)).toBeVisible({ timeout: 15_000 });

  await patchExaminerDraftMemoryPolicy(page, forkBody.draftId);
  await page.goto("/admin/definitions/examiner");
  const refreshed = await reopenExaminerDraftByMarker(page, marker);

  await completeDefinitionDraftPublishGate(page, refreshed, "knowledge.retrieve", {
    skipToolAllowlist: true
  });
  await publishDraftFromInstructions(page, refreshed);
  const publishedToast = page.getByText(/Published version \d+/).first();
  await expect(publishedToast).toBeVisible({ timeout: 15_000 });
  const version = (await publishedToast.textContent())?.match(/Published version (\d+)/)?.[1];
  expect(version).toBeTruthy();

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
  await refreshed.getByRole("button", { name: `Start managed chat for v${version}` }).click();
  const instance = (await (await instanceResponsePromise).json()) as ManagedInstance;
  const firstSession = (await (await sessionResponsePromise).json()) as SessionView;

  expect(instance.compatibility).toBe(false);
  expect(firstSession.agentInstanceId).toBe(instance.instanceId);
  expect(firstSession.agentVersion).toBe(Number(version));

  await expect(page).toHaveURL(/\/c\/[0-9a-f-]+/i, { timeout: 20_000 });
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 20_000 });

  return {
    instance,
    firstSession,
    marker,
    publishedVersion: Number(version),
    resourcePath,
    resourceBody
  };
}

export async function startSyntheticChat(page: Page) {
  await page.goto("/");
  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Hello from synthetic.")).toBeVisible({ timeout: 15_000 });
  await page.waitForFunction(() => window.localStorage.getItem("agent-core.owner-capability"));
}

export async function publishExaminerDraftAndCreateManagedInstance(page: Page) {
  await page.getByRole("button", { name: "Open Admin" }).click();
  await expect(page).toHaveURL(/\/admin$/);
  await page.locator('section[aria-label="Definitions"]').getByRole("button", { name: /examiner/i }).first().click();

  const draftsSection = page.locator('section[aria-label="Definition drafts"]');
  await page.getByRole("button", { name: /Fork v1 \(builtIn\)/ }).click();
  await expect(draftsSection.getByLabel("System instructions")).toBeVisible({ timeout: 15_000 });
  const marker = `p7d-managed-${Date.now()}`;
  await draftsSection.getByLabel("System instructions").fill(marker);
  await draftsSection.getByRole("button", { name: "Save draft" }).click();
  await expect(page.getByText("Draft saved.")).toBeVisible({ timeout: 15_000 });

  await completeDefinitionDraftPublishGate(page, draftsSection);
  await publishDraftFromInstructions(page, draftsSection);
  const publishedToast = page.getByText(/Published version \d+/);
  await expect(publishedToast).toBeVisible({ timeout: 15_000 });
  const version = (await publishedToast.textContent())?.match(/Published version (\d+)/)?.[1];
  expect(version).toBeTruthy();

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
  await draftsSection.getByRole("button", { name: `Start managed chat for v${version}` }).click();
  const instanceResponse = await instanceResponsePromise;
  const sessionResponse = await sessionResponsePromise;
  const instance = (await instanceResponse.json()) as ManagedInstance;
  const firstSession = (await sessionResponse.json()) as SessionView;

  expect(instance.compatibility).toBe(false);
  expect(instance.definitionId).toBe("examiner");
  expect(instance.personaRevision).toBe(1);
  expect(firstSession.agentInstanceId).toBe(instance.instanceId);
  expect(firstSession.agentId).toBe("examiner");
  expect(firstSession.agentVersion).toBe(Number(version));
  expect(firstSession.pinnedPersonaRevision).toBe(1);

  await expect(page).toHaveURL(/\/c\/[0-9a-f-]+/i, { timeout: 20_000 });
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 20_000 });

  return {
    instance,
    firstSession,
    marker,
    publishedVersion: Number(version)
  };
}

export function managedInstanceShortId(instanceId: string): string {
  return instanceId.replace(/-/g, "").toLowerCase().slice(-8);
}

export async function selectManagedIdentityOption(page: Page, instanceId: string) {
  const shortId = managedInstanceShortId(instanceId);
  await expect(page.getByLabel("Loading managed instances")).toBeHidden({ timeout: 15_000 });
  const combobox = page.getByRole("combobox", { name: "Identity" });
  await combobox.click();
  await combobox.pressSequentially(shortId, { delay: 20 });
  const option = page.locator(".ant-select-item-option").filter({ hasText: shortId });
  await expect(option.first()).toBeVisible({ timeout: 15_000 });
  await option.first().click();
}

export async function expectManagedIdentityOptionAbsent(page: Page, instanceId: string) {
  const shortId = managedInstanceShortId(instanceId);
  await page.getByRole("combobox", { name: "Identity" }).click();
  await expect(page.locator(".ant-select-item-option", { hasText: shortId })).toHaveCount(0);
  await page.keyboard.press("Escape");
}

export async function openExaminerDefinitionDrafts(page: Page) {
  await page.goto("/admin/definitions/examiner");
  await expect(page).toHaveURL(/\/admin\/definitions\/examiner$/i);
  return page.locator('section[aria-label="Definition drafts"]');
}

/** Fork a durable publication, run the publish gate, and return the new immutable version number. */
export async function publishExaminerForkedVersion(page: Page, forkFromVersion: number): Promise<number> {
  const draftsSection = await openExaminerDefinitionDrafts(page);
  await page.getByRole("button", { name: `Fork v${forkFromVersion} (durable)` }).click();
  const instructions = draftsSection.getByLabel("System instructions");
  await expect(instructions).toBeVisible({ timeout: 15_000 });
  const marker = `p7g-next-${Date.now()}`;
  const prior = (await instructions.inputValue()) || "";
  await instructions.fill(`${prior}\n${marker}`);
  await ensureToolAllowlisted(page, draftsSection, "knowledge.retrieve");
  await draftsSection.getByRole("button", { name: "Save draft" }).click();
  await expect(page.getByText("Draft saved.").first()).toBeVisible({ timeout: 15_000 });
  await completeDefinitionDraftPublishGate(page, draftsSection);
  await publishDraftFromInstructions(page, draftsSection);
  const publishedToast = page.getByText(/Published version \d+/).first();
  await expect(publishedToast).toBeVisible({ timeout: 15_000 });
  const version = (await publishedToast.textContent())?.match(/Published version (\d+)/)?.[1];
  expect(version).toBeTruthy();
  return Number(version);
}

/** Wait for the durable persona PATCH contract instead of overlapping Ant Design toasts. */
export async function savePersonaAndAwaitPatch(page: Page, instanceId: string) {
  const responsePromise = page.waitForResponse(
    (response) =>
      response.request().method() === "PATCH" &&
      response.url().includes(`/api/v2/admin/agent-instances/${instanceId}/persona`) &&
      response.ok(),
    { timeout: 30_000 }
  );
  await page.getByRole("button", { name: "Save persona" }).click();
  return await responsePromise;
}
