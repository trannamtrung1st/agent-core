import { expect, test } from "@playwright/test";

for (const scope of ["scratch", "home"]) test(`native ${scope} filesystem batches require approval and preserve complete trees`, async ({ page }) => {
  test.setTimeout(120_000);
  await page.goto("/");
  await expect.poll(() => page.evaluate(() => localStorage.getItem("agent-core.owner-capability"))).not.toBeNull();
  const token = await page.evaluate(() => localStorage.getItem("agent-core.owner-capability"));
  const headers = { "X-AgentCore-Owner-Capability": token! };
  const created = await page.request.post("/api/v2/admin/agent-instances", { headers, data: { definitionId: "general-assistant", version: 17 } });
  expect(created.ok(), await created.text()).toBe(true); const owner = (await created.json()).instanceId;
  const opened = await page.request.post("/api/v2/sessions", { headers, data: { agentInstanceId: owner, mode: "text", modelCatalogKey: "scripted-alpha" } });
  expect(opened.ok()).toBe(true); const session = (await opened.json()).sessionId;
  const root = scope === "home" ? "/home" : "/working";
  const binary = Buffer.from([0, 255, 128, 13, 10]);
  for (const [name, bytes] of [["text.txt", Buffer.from("hello café\r\n")], ["sub/raw.bin", binary]] as const) {
    const path = `${root}/inbox/${name}`;
    expect((await page.request.put(`/api/v2/sessions/${session}/workspace/content?path=${path}`, { headers, data: bytes })).ok()).toBe(true);
  }
  async function openSession() {
    await page.goto(`/c/${session}`);
    // The new-chat shell also says Ready before bootstrap opens the URL's Session.
    // Wait for a Session-only control before checking its transport readiness.
    await expect(page.getByRole("button", { name: "Background work", exact: true })).toBeVisible();
    await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 20_000 });
  }
  await openSession();
  async function send(command: string) {
    const text = `synthetic-workspace-filesystem:${command}`;
    await page.getByLabel("Message").fill(text);
    await expect(page.getByLabel("Message")).toHaveValue(text);
    await page.getByRole("button", { name: "Send", exact: true }).click();
  }
  await send(scope);
  const approval = page.getByRole("dialog", { name: "Approve workspace changes?" });
  await expect(approval).toContainText(`${root}/projects/project`, { timeout: 20_000 });
  await expect(approval).toContainText("Earlier changes remain");
  await approval.getByRole("button", { name: "Reject", exact: true }).click();
  await expect(approval).toBeHidden();
  await expect(page.locator(".chat-message-assistant").last()).toContainText("Filesystem result:", { timeout: 20_000 });
  expect((await page.request.get(`/api/v2/sessions/${session}/workspace/content?path=${root}/inbox/sub/raw.bin`, { headers })).ok()).toBe(true);
  expect((await page.request.get(`/api/v2/sessions/${session}/workspace/content?path=${root}/backup/sub/raw.bin`, { headers })).status()).toBe(404);
  await send(scope); await expect(approval).toBeVisible();
  await approval.getByRole("button", { name: "Approve", exact: true }).click();
  await expect(page.locator(".chat-message-assistant").last()).toContainText('"completedCount":4', { timeout: 25_000 });
  for (const path of [`${root}/projects/project/sub/raw.bin`, `${root}/backup/sub/raw.bin`])
    expect(await (await page.request.get(`/api/v2/sessions/${session}/workspace/content?path=${path}`, { headers })).body()).toEqual(binary);
  const nodes = await (await page.request.get(`/api/v2/sessions/${session}/workspace?prefix=${root}/backup/empty`, { headers })).json();
  expect(nodes).toContainEqual(expect.objectContaining({ logicalPath: `${root}/backup/empty/deep`, directory: true }));
  if (scope === "home") {
    await page.goto(`/admin/instances/${owner}/identity`); await page.getByRole("tab", { name: "Workspace", exact: true }).click();
    await expect(page.getByRole("button", { name: `Download ${root}/backup/empty/deep`, exact: true })).toBeDisabled();
    await expect(page.getByText(`${root}/backup/empty/deep`, { exact: true })).toBeVisible();
    await openSession();
  }
  await send(`${scope} delete`); await expect(approval).toContainText('"recursive":true');
  await approval.getByRole("button", { name: "Approve", exact: true }).click();
  await expect(page.locator(".chat-message-assistant").last()).toContainText('"completedCount":1', { timeout: 25_000 });
  expect((await page.request.get(`/api/v2/sessions/${session}/workspace/content?path=${root}/backup/sub/raw.bin`, { headers })).status()).toBe(404);
  expect(await (await page.request.get(`/api/v2/sessions/${session}/workspace/content?path=${root}/projects/project/sub/raw.bin`, { headers })).body()).toEqual(binary);
});
