import { expect, test, type APIRequestContext, type Page } from "@playwright/test";
import {
  expectManagedIdentityOptionAbsent,
  publishExaminerDraftAndCreateManagedInstance,
  selectManagedIdentityOption,
  startSyntheticChat,
  type SessionView
} from "./admin-managed-helpers";

const antdNoise = (line: string) =>
  line.includes("[antd: List]") || line.includes("[antd: Alert]");

type EffectiveConfig = {
  definitionId: string;
  definitionVersion: number;
  instanceId: string;
  personaRevision: number;
  persona: { name: string; role: string; description: string; tone: string };
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

test("p7d managed instance persona form json chat archive and history", async ({ page, request }) => {
  test.setTimeout(120_000);
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

  const secondSessionPromise = page.waitForResponse(
    (response) =>
      response.request().method() === "POST" &&
      /\/api\/v2\/sessions$/.test(response.url()) &&
      response.ok()
  );
  await selectManagedIdentityOption(page, instance.instanceId);
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
  await expectManagedIdentityOptionAbsent(page, instance.instanceId);

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
