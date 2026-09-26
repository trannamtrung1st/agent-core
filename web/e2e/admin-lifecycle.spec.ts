/**
 * Frozen P7G §8 whole-phase deterministic Admin lifecycle (26 steps).
 * Requires disposable SQLite for memory/trigger/work seeds: PLAYWRIGHT_SQLITE_PATH=<path>.
 */
import { expect, test, type APIRequestContext, type Page } from "@playwright/test";
import {
  seedActiveTriggerRegistration,
  seedCompletedHistoricalWorkItem,
  seedIdentityLearnedMemory
} from "./admin-lifecycle-sqlite";
import {
  expectManagedIdentityOptionAbsent,
  publishExaminerForkedVersion,
  publishExaminerP7gFirstPublication,
  savePersonaAndAwaitPatch,
  selectManagedIdentityOption,
  startSyntheticChat,
  type SessionView
} from "./admin-managed-helpers";

const dbPath = process.env.PLAYWRIGHT_SQLITE_PATH ?? "";
const scheduleIntent = "P7G lifecycle reminder";

const antdNoise = (line: string) =>
  line.includes("[antd: List]") || line.includes("[antd: Alert]");

async function ownerCapability(page: Page): Promise<string> {
  return page.evaluate(() => window.localStorage.getItem("agent-core.owner-capability") ?? "");
}

async function ownerHeaders(page: Page) {
  return { "X-AgentCore-Owner-Capability": await ownerCapability(page) };
}

async function ensureProfileUtc(page: Page) {
  await page.evaluate(async () => {
    const token = window.localStorage.getItem("agent-core.owner-capability") ?? "";
    const headers = {
      "content-type": "application/json",
      "X-AgentCore-Owner-Capability": token
    };
    const current = await fetch("/api/v2/profile", { headers });
    if (!current.ok) {
      throw new Error(`profile ${current.status}`);
    }
    const profile = (await current.json()) as { revision: number };
    const patched = await fetch("/api/v2/profile", {
      method: "PATCH",
      headers,
      body: JSON.stringify({ expectedRevision: profile.revision, values: { timeZone: "UTC" } })
    });
    if (!patched.ok) {
      throw new Error(await patched.text());
    }
  });
}

async function readWorkspaceText(page: Page, sessionId: string, logicalPath: string) {
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

type EffectiveConfig = {
  definitionVersion: number;
  personaRevision: number;
  persona: { name: string; tone: string };
  durableExecutionEligibility: { canAcceptNewTriggeredWork: boolean };
};

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

type AdminEventRow = { operation: string };

async function listAdminEvents(
  request: APIRequestContext,
  page: Page,
  query: string
): Promise<AdminEventRow[]> {
  const response = await request.get(`/api/v2/admin/events?${query}`, {
    headers: await ownerHeaders(page)
  });
  expect(response.ok()).toBe(true);
  const body = (await response.json()) as { items: AdminEventRow[] };
  return body.items;
}

test("p7g whole-phase admin lifecycle per frozen contract section 8", async ({ page, request }) => {
  test.setTimeout(300_000);
  expect(dbPath.length).toBeGreaterThan(0);

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

  const personaName = `P7G Persona ${Date.now()}`;
  const jsonTone = `p7g-tone-${Date.now()}`;

  await startSyntheticChat(page);
  await ensureProfileUtc(page);

  const { instance, firstSession, publishedVersion: versionOne, resourcePath, resourceBody } =
    await publishExaminerP7gFirstPublication(page);

  const resourceContent = await readWorkspaceText(page, firstSession.sessionId, `/agent/resources/${resourcePath}`);
  expect(resourceContent).toContain(resourceBody);

  await page.getByLabel("Message").fill("First pinned managed turn");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Hello from synthetic.")).toBeVisible({ timeout: 15_000 });

  await page.getByRole("button", { name: "Open Admin" }).click();
  await page.goto(`/admin/instances/${instance.instanceId}`);

  await page.getByLabel("Persona name").fill(personaName);
  await savePersonaAndAwaitPatch(page, instance.instanceId);
  let afterFormSave = await fetchEffectiveConfig(request, page, instance.instanceId);
  expect(afterFormSave.persona.name).toBe(personaName);
  expect(afterFormSave.personaRevision).toBe(2);

  await page.getByRole("tab", { name: "JSON" }).click();
  const personaJson = page.getByLabel("Persona JSON");
  const parsed = JSON.parse(await personaJson.inputValue()) as {
    name: string;
    role: string;
    description: string;
    tone: string;
  };
  parsed.tone = jsonTone;
  await personaJson.fill(JSON.stringify(parsed, null, 2));
  await savePersonaAndAwaitPatch(page, instance.instanceId);

  const afterPersona = await fetchEffectiveConfig(request, page, instance.instanceId);
  expect(afterPersona.definitionVersion).toBe(versionOne);
  expect(afterPersona.persona.name).toBe(personaName);
  expect(afterPersona.persona.tone).toBe(jsonTone);
  expect(afterPersona.personaRevision).toBe(3);

  const memoryContent = `seed-${Date.now()}`;
  seedIdentityLearnedMemory(instance.instanceId, firstSession.sessionId, "P7G identity fact", memoryContent);
  const registrationId = seedActiveTriggerRegistration(
    instance.instanceId,
    firstSession.sessionId,
    scheduleIntent
  );

  const memoryAutomation = page.getByLabel("Memory and automation administration");
  await memoryAutomation.getByRole("tab", { name: "Memory" }).click();
  await memoryAutomation.getByRole("combobox", { name: "Memory scope" }).click();
  await page.locator(".ant-select-item-option", { hasText: "IdentityUser" }).click();
  await memoryAutomation.getByRole("button", { name: "Load items" }).click();
  await expect(memoryAutomation.getByText(memoryContent)).toBeVisible({ timeout: 15_000 });
  await memoryAutomation.getByRole("button", { name: "Reset scope" }).click();
  await page.locator(".ant-popconfirm-buttons").getByRole("button", { name: "Reset scope" }).click();
  await expect(memoryAutomation.getByText("No active learned-memory items in this scope.")).toBeVisible({
    timeout: 15_000
  });

  await memoryAutomation.getByRole("tab", { name: "Automation" }).click();
  await memoryAutomation.getByRole("button", { name: "Load registrations" }).click();
  await expect(memoryAutomation.getByText(scheduleIntent)).toBeVisible({ timeout: 15_000 });
  await memoryAutomation.getByRole("button", { name: "Revoke" }).click();
  await page.locator(".ant-popconfirm-buttons").getByRole("button", { name: "Cancel registration" }).click();
  await expect(memoryAutomation.getByText("No active or suspended registrations.")).toBeVisible({
    timeout: 15_000
  });

  await page.getByRole("button", { name: /Chat$/ }).click();
  await page.getByRole("button", { name: "Start a new chat" }).click();
  await expect(page.getByRole("combobox", { name: "Identity" })).toBeEnabled({ timeout: 15_000 });

  const secondSessionPromise = page.waitForResponse(
    (response) =>
      response.request().method() === "POST" &&
      /\/api\/v2\/sessions$/.test(response.url()) &&
      response.ok()
  );
  await selectManagedIdentityOption(page, instance.instanceId);
  await page.getByLabel("Message").fill("Second managed instance chat");
  await page.getByRole("button", { name: "Send" }).click();
  const secondSession = (await (await secondSessionPromise).json()) as SessionView;
  await expect(page.getByText("Hello from synthetic.")).toBeVisible({ timeout: 15_000 });
  expect(secondSession.agentInstanceId).toBe(instance.instanceId);
  expect(secondSession.agentVersion).toBe(versionOne);
  expect(secondSession.pinnedPersonaRevision).toBe(3);

  const versionTwo = await publishExaminerForkedVersion(page, versionOne);
  expect(versionTwo).toBeGreaterThan(versionOne);

  const pinnedAfterV2 = await fetchSessionView(request, page, firstSession.sessionId);
  expect(pinnedAfterV2.agentVersion).toBe(versionOne);
  expect(pinnedAfterV2.pinnedPersonaRevision).toBe(1);

  const historicalWorkItemId = seedCompletedHistoricalWorkItem(instance.instanceId, firstSession.sessionId);

  await page.goto(`/admin/instances/${instance.instanceId}`);
  await page.getByLabel("Target definition version").click();
  await page.getByText(new RegExp(`v${versionTwo}\\b`)).first().click();
  await page.getByRole("button", { name: `Upgrade to v${versionTwo}` }).click();
  await expect(page.getByText(`Active version set to v${versionTwo}.`)).toBeVisible({ timeout: 15_000 });

  await page.getByLabel("Target definition version").click();
  await page.getByText(new RegExp(`v${versionOne}\\b`)).first().click();
  await page.getByRole("button", { name: `Rollback to v${versionOne}` }).click();
  await expect(page.getByText(`Active version set to v${versionOne}.`)).toBeVisible({ timeout: 15_000 });

  await page.goto("/admin/definitions/examiner");
  await page.getByRole("button", { name: `Deprecate publication v${versionTwo}` }).click();
  await page.getByRole("button", { name: "Deprecate", exact: true }).click();
  await expect(page.getByText(`Publication v${versionTwo} deprecated.`)).toBeVisible({ timeout: 15_000 });

  await page.goto(`/admin/instances/${instance.instanceId}`);
  await page.getByRole("button", { name: "Archive instance" }).click();
  await page.getByRole("button", { name: "Archive", exact: true }).click();
  await expect(page.getByText("Instance archived.")).toBeVisible({ timeout: 15_000 });

  expect((await fetchEffectiveConfig(request, page, instance.instanceId)).durableExecutionEligibility
    .canAcceptNewTriggeredWork).toBe(false);

  const deniedCreate = await request.post("/api/v2/sessions", {
    headers: {
      "Content-Type": "application/json",
      ...(await ownerHeaders(page))
    },
    data: { agentInstanceId: instance.instanceId, mode: "text" },
    failOnStatusCode: false
  });
  expect(deniedCreate.status()).toBe(400);

  const workItems = await request.get(`/api/v2/sessions/${firstSession.sessionId}/work-items`, {
    headers: await ownerHeaders(page)
  });
  expect(workItems.ok()).toBe(true);
  const workBody = (await workItems.json()) as { items: { workItemId: string; status: string }[] };
  const historical = workBody.items.find((item) => item.workItemId === historicalWorkItemId);
  expect(historical).toBeTruthy();
  expect(historical?.status).toBe("completed");

  await page.getByRole("button", { name: /Chat$/ }).click();
  await page.getByRole("button", { name: "Start a new chat" }).click();
  await expect(page.getByRole("combobox", { name: "Identity" })).toBeEnabled({ timeout: 15_000 });
  await expectManagedIdentityOptionAbsent(page, instance.instanceId);

  const instanceEvents = await listAdminEvents(
    request,
    page,
    `targetType=agent.instance&targetId=${instance.instanceId}`
  );
  const operations = new Set(instanceEvents.map((item) => item.operation));
  expect(operations.has("PersonaChanged")).toBe(true);
  expect(operations.has("MemoryScopeReset")).toBe(true);
  expect(operations.has("InstanceDefinitionVersionChanged")).toBe(true);
  expect(operations.has("InstanceArchived")).toBe(true);

  const publicationEvents = await listAdminEvents(
    request,
    page,
    `targetType=definition.publication&targetId=examiner:${versionTwo}`
  );
  expect(publicationEvents.some((item) => item.operation === "PublicationDeprecated")).toBe(true);

  const registrationEvents = await listAdminEvents(
    request,
    page,
    `targetType=trigger.registration&targetId=${registrationId}`
  );
  expect(registrationEvents.some((item) => item.operation === "TriggerRegistrationRevoked")).toBe(true);

  expect(failedRequests.filter((item) => !item.includes("favicon"))).toEqual([]);
  expect(
    consoleErrors.filter((line) => !antdNoise(line) && !line.includes("403 (Forbidden)"))
  ).toEqual([]);
});
