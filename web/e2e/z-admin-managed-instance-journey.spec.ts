import { expect, test, type APIRequestContext, type Page } from "@playwright/test";

const antdNoise = (line: string) =>
  line.includes("[antd: List]") || line.includes("[antd: Alert]");

type SessionView = {
  sessionId: string;
  agentId: string;
  agentVersion: number;
  agentInstanceId: string | null;
  pinnedPersonaRevision: number | null;
};

type EffectiveConfig = {
  definitionId: string;
  definitionVersion: number;
  instanceId: string;
  personaRevision: number;
  persona: { name: string; role: string; description: string; tone: string };
};

type ManagedInstance = {
  instanceId: string;
  definitionId: string;
  activeVersion: number;
  compatibility: boolean;
  personaRevision: number;
};

async function ownerCapability(page: Page): Promise<string> {
  return page.evaluate(() => window.localStorage.getItem("agent-core.owner-capability") ?? "");
}

async function ownerHeaders(page: Page) {
  return { "X-AgentCore-Owner-Capability": await ownerCapability(page) };
}

async function fetchSessionView(
  request: APIRequestContext,
  page: Page,
  sessionId: string
): Promise<SessionView> {
  const response = await request.get(`/api/v2/sessions/${sessionId}`, {
    headers: await ownerHeaders(page)
  });
  expect(response.ok()).toBe(true);
  return (await response.json()) as SessionView;
}

async function fetchEffectiveConfig(
  request: APIRequestContext,
  page: Page,
  instanceId: string
): Promise<EffectiveConfig> {
  const response = await request.get(`/api/v2/admin/instances/${instanceId}/effective-config`, {
    headers: await ownerHeaders(page)
  });
  expect(response.ok()).toBe(true);
  return (await response.json()) as EffectiveConfig;
}

async function startSyntheticChat(page: Page) {
  await page.goto("/");
  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Hello from synthetic.")).toBeVisible({ timeout: 15_000 });
  await page.waitForFunction(() => window.localStorage.getItem("agent-core.owner-capability"));
}

async function publishExaminerDraftAndCreateManagedInstance(page: Page) {
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

test("p7d managed instance persona form json chat archive and history", async ({ page, request }) => {
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

  const personaName = `P7D Persona ${Date.now()}`;
  const jsonTone = `json-tone-${Date.now()}`;
  await startSyntheticChat(page);
  const { instance, firstSession, publishedVersion } = await publishExaminerDraftAndCreateManagedInstance(page);

  const initialEffective = await fetchEffectiveConfig(request, page, instance.instanceId);
  const pinnedPersonaName = initialEffective.persona.name;
  const pinnedPersonaRole = initialEffective.persona.role;
  expect(initialEffective.definitionVersion).toBe(publishedVersion);
  expect(initialEffective.personaRevision).toBe(1);

  await page.getByLabel("Message").fill("First managed session");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Hello from synthetic.")).toBeVisible({ timeout: 15_000 });
  const firstChatUrl = page.url();
  const shortId = instance.instanceId.replace(/-/g, "").toLowerCase().slice(-8);

  await page.getByRole("button", { name: "Open Admin" }).click();
  await page.goto(`/admin/instances/${instance.instanceId}`);
  await expect(page.getByText("Managed")).toBeVisible();

  await page.getByLabel("Persona name").fill(personaName);
  await page.getByRole("button", { name: "Save persona" }).click();
  await expect(page.getByText("Persona updated.")).toBeVisible({ timeout: 15_000 });

  await page.getByRole("tab", { name: "JSON" }).click();
  const personaJson = page.getByLabel("Persona JSON");
  await expect(personaJson).toHaveValue(new RegExp(personaName));
  const parsed = JSON.parse(await personaJson.inputValue()) as {
    name: string;
    role: string;
    description: string;
    tone: string;
  };
  parsed.tone = jsonTone;
  await personaJson.fill(JSON.stringify(parsed, null, 2));
  await page.getByRole("button", { name: "Save persona" }).click();
  await expect(page.getByText("Persona updated.")).toBeVisible({ timeout: 15_000 });

  await page.getByRole("tab", { name: "Form" }).click();
  await expect(page.getByLabel("Persona name")).toHaveValue(personaName);
  await expect(page.getByLabel("Persona tone")).toHaveValue(jsonTone);

  const afterPersonaEdits = await fetchEffectiveConfig(request, page, instance.instanceId);
  expect(afterPersonaEdits.definitionId).toBe("examiner");
  expect(afterPersonaEdits.definitionVersion).toBe(publishedVersion);
  expect(afterPersonaEdits.persona.name).toBe(personaName);
  expect(afterPersonaEdits.persona.tone).toBe(jsonTone);
  expect(afterPersonaEdits.personaRevision).toBe(3);

  await page.getByRole("button", { name: "Chat", exact: true }).click();
  await page.getByRole("button", { name: "Start a new chat" }).click();
  await expect(page.getByRole("combobox", { name: "Identity" })).toBeEnabled({ timeout: 15_000 });
  await page.getByRole("combobox", { name: "Identity" }).click();
  await page.locator(".ant-select-item-option", { hasText: shortId }).click();

  const secondSessionPromise = page.waitForResponse(
    (response) =>
      response.request().method() === "POST" &&
      /\/api\/v2\/sessions$/.test(response.url()) &&
      response.ok()
  );
  await page.getByLabel("Message").fill("Managed instance turn");
  await page.getByRole("button", { name: "Send" }).click();
  const secondSession = (await (await secondSessionPromise).json()) as SessionView;
  await expect(page.getByText("Hello from synthetic.")).toBeVisible({ timeout: 15_000 });
  const secondChatUrl = page.url();
  expect(secondChatUrl).not.toBe(firstChatUrl);

  expect(secondSession.agentInstanceId).toBe(instance.instanceId);
  expect(secondSession.agentVersion).toBe(publishedVersion);
  expect(secondSession.pinnedPersonaRevision).toBe(3);

  await page.goto(`/admin/instances/${instance.instanceId}`);
  await page.getByRole("button", { name: "Archive instance" }).click();
  await page.getByRole("button", { name: "Archive", exact: true }).click();
  await expect(page.getByText("Instance archived.")).toBeVisible({ timeout: 15_000 });
  await expect(page.locator(".ant-tag", { hasText: "Archived" }).first()).toBeVisible();

  const deniedCreate = await request.post("/api/v2/sessions", {
    headers: {
      "Content-Type": "application/json",
      ...(await ownerHeaders(page))
    },
    data: { agentInstanceId: instance.instanceId, mode: "text" },
    failOnStatusCode: false
  });
  expect(deniedCreate.status()).toBe(400);
  const deniedBody = (await deniedCreate.json()) as { title?: string; detail?: string; code?: string };
  expect(deniedBody.title ?? deniedBody.code).toBe("ValidationError");
  expect(deniedBody.detail).toContain("Archived agent instances cannot start new sessions");

  await page.getByRole("button", { name: "Chat", exact: true }).click();
  await page.getByRole("button", { name: "Start a new chat" }).click();
  await expect(page.getByRole("combobox", { name: "Identity" })).toBeEnabled({ timeout: 15_000 });
  await page.getByRole("combobox", { name: "Identity" }).click();
  await expect(page.locator(".ant-select-item-option", { hasText: shortId })).toHaveCount(0);
  await page.keyboard.press("Escape");

  const firstPinned = await fetchSessionView(request, page, firstSession.sessionId);
  const secondPinned = await fetchSessionView(request, page, secondSession.sessionId);
  expect(firstPinned.agentInstanceId).toBe(instance.instanceId);
  expect(secondPinned.agentInstanceId).toBe(instance.instanceId);
  expect(firstPinned.pinnedPersonaRevision).toBe(1);
  expect(secondPinned.pinnedPersonaRevision).toBe(3);
  expect(firstPinned.pinnedPersonaRevision).not.toBe(secondPinned.pinnedPersonaRevision);

  await page.goto(firstChatUrl);
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 20_000 });
  expect(firstPinned.agentVersion).toBe(publishedVersion);
  expect(pinnedPersonaName).not.toBe(personaName);
  await expect(page.locator(".chat-header-title")).toContainText(pinnedPersonaName);
  await expect(page.locator(".chat-header-subtitle")).toContainText(pinnedPersonaRole);
  await expect(page.locator(".conversation-scroll").getByText("First managed session", { exact: true })).toBeVisible({
    timeout: 15_000
  });

  await page.goto(secondChatUrl);
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 20_000 });
  expect(secondPinned.agentVersion).toBe(publishedVersion);
  expect(afterPersonaEdits.persona.name).toBe(personaName);
  await expect(page.locator(".chat-header-title")).toContainText(personaName);
  await expect(page.locator(".chat-header-subtitle")).toContainText(afterPersonaEdits.persona.role);
  await expect(page.locator(".conversation-scroll").getByText("Managed instance turn", { exact: true })).toBeVisible({
    timeout: 15_000
  });

  expect(publishedVersion).toBeGreaterThan(0);

  expect(failedRequests.filter((item) => !item.includes("favicon"))).toEqual([]);
  expect(consoleErrors.filter((line) => !antdNoise(line))).toEqual([]);
});
