/**
 * Partial P7G browser slice (frozen contract §8 is 26 steps in one scenario).
 * This spec covers: managed publish + chat pin, memory/automation tab load, second
 * publication, session version pinning, upgrade/rollback, deprecate, archive, denied
 * new managed session, and admin events API assertions.
 * Omitted here (covered elsewhere or still under Remaining): resource upload,
 * persona Form/JSON, memory scope reset, schedule revoke, triggered/headless denial,
 * historical work preservation — see z-admin-*-journey.spec.ts and docs/reports/p7g-history-rollback-final-gate.md.
 */
import { expect, test, type APIRequestContext, type Page } from "@playwright/test";
import {
  expectManagedIdentityOptionAbsent,
  publishExaminerDraftAndCreateManagedInstance,
  publishExaminerForkedVersion,
  startSyntheticChat,
  type SessionView
} from "./admin-managed-helpers";

const antdNoise = (line: string) =>
  line.includes("[antd: List]") || line.includes("[antd: Alert]");

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

test("p7g partial admin lifecycle: version pin, upgrade or rollback, deprecate, archive, history", async ({
  page,
  request
}) => {
  test.setTimeout(180_000);
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

  await startSyntheticChat(page);
  const { instance, firstSession, publishedVersion: versionOne } =
    await publishExaminerDraftAndCreateManagedInstance(page);

  await page.getByLabel("Message").fill("Lifecycle pinned session");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Hello from synthetic.")).toBeVisible({ timeout: 15_000 });

  await page.getByRole("button", { name: "Open Admin" }).click();
  await page.goto(`/admin/instances/${instance.instanceId}`);
  const memoryAutomation = page.getByLabel("Memory and automation administration");
  await memoryAutomation.getByRole("tab", { name: "Memory" }).click();
  await memoryAutomation.getByRole("combobox", { name: "Memory scope" }).click();
  await page.locator(".ant-select-item-option", { hasText: "Session" }).click();
  await memoryAutomation.getByRole("textbox", { name: "Session id" }).fill(firstSession.sessionId);
  await memoryAutomation.getByRole("button", { name: "Load items" }).click();
  await memoryAutomation.getByRole("tab", { name: "Automation" }).click();
  await memoryAutomation.getByRole("button", { name: "Load registrations" }).click();

  const versionTwo = await publishExaminerForkedVersion(page, versionOne);
  expect(versionTwo).toBeGreaterThan(versionOne);

  const pinnedAfterV2Publish = await fetchSessionView(request, page, firstSession.sessionId);
  expect(pinnedAfterV2Publish.agentVersion).toBe(versionOne);
  expect(pinnedAfterV2Publish.agentInstanceId).toBe(instance.instanceId);

  await page.goto(`/admin/instances/${instance.instanceId}`);
  await expect(page.getByLabel("Managed instance controls")).toBeVisible();
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

  const deniedCreate = await request.post("/api/v2/sessions", {
    headers: {
      "Content-Type": "application/json",
      ...(await ownerHeaders(page))
    },
    data: { agentInstanceId: instance.instanceId, mode: "text" },
    failOnStatusCode: false
  });
  expect(deniedCreate.status()).toBe(400);

  await page.getByRole("button", { name: "Chat", exact: true }).click();
  await page.getByRole("button", { name: "Start a new chat" }).click();
  await expect(page.getByRole("combobox", { name: "Identity" })).toBeEnabled({ timeout: 15_000 });
  await expectManagedIdentityOptionAbsent(page, instance.instanceId);

  const instanceEvents = await listAdminEvents(
    request,
    page,
    `targetType=agent.instance&targetId=${instance.instanceId}`
  );
  const operations = new Set(instanceEvents.map((item) => item.operation));
  expect(operations.has("InstanceDefinitionVersionChanged")).toBe(true);
  expect(operations.has("InstanceArchived")).toBe(true);

  const publicationEvents = await listAdminEvents(
    request,
    page,
    `targetType=definition.publication&targetId=examiner:${versionTwo}`
  );
  expect(publicationEvents.some((item) => item.operation === "PublicationDeprecated")).toBe(true);

  expect(failedRequests.filter((item) => !item.includes("favicon"))).toEqual([]);
  expect(
    consoleErrors.filter(
      (line) => !antdNoise(line) && !line.includes("403 (Forbidden)")
    )
  ).toEqual([]);
});
