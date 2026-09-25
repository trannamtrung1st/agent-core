import { expect, type Page } from "@playwright/test";

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

  await draftsSection.getByRole("button", { name: "Publish…" }).click();
  const modal = page.getByRole("dialog");
  await modal.getByRole("button", { name: "Publish" }).click();
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
  await page.getByRole("combobox", { name: "Identity" }).click();
  const option = page.locator(".ant-select-item-option").filter({ hasText: shortId });
  await expect(option.first()).toBeVisible({ timeout: 15_000 });
  await option.first().click();
}

export async function expectManagedIdentityOptionAbsent(page: Page, instanceId: string) {
  const shortId = managedInstanceShortId(instanceId);
  await page.getByRole("combobox", { name: "Identity" }).click();
  await expect(page.locator(".ant-select-item-option").filter({ hasText: shortId })).toHaveCount(0);
  await page.keyboard.press("Escape");
}
