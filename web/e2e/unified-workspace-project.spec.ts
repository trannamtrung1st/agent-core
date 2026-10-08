import { expect, test } from "@playwright/test";
import { readFile } from "node:fs/promises";
import { selectInstanceIdentity, INSTANCE_DEFINITIONS } from "./support/instance-identity";

test("Chat copies and renames an exact four-file project, edits with CAS, and preserves home across session deletion", async ({ page }) => {
  test.setTimeout(150_000);
  await page.goto("/");
  await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.generalAssistant);
  const token = (await page.evaluate(() => localStorage.getItem("agent-core.owner-capability")))!;
  const headers = { "X-AgentCore-Owner-Capability": token };
  async function send(command: string) {
    const count = await page.locator(".chat-message-assistant").count();
    const message = `synthetic-agent-workspace-v2:${command}`;
    await expect(async () => {
      await page.getByLabel("Message").fill(message);
      await expect(page.getByLabel("Message")).toHaveValue(message);
      await expect(page.getByRole("button", { name: "Send", exact: true })).toBeEnabled();
    }).toPass();
    await page.getByRole("button", { name: "Send", exact: true }).click();
    await expect(page.locator(".chat-message-assistant")).toHaveCount(count + 1, { timeout: 45_000 });
    const answer = page.locator(".chat-message-assistant").last();
    await expect(answer).toContainText("Agent Workspace v2 results:", { timeout: 45_000 });
    await expect(answer).not.toContainText('"error":');
    return answer;
  }
  const answer = await send("project");
  await expect(answer).toContainText('"cwd":"/home"');
  await expect(answer).toContainText('"filesAffected":4');
  const first = page.url().split("/c/")[1];
  const firstView = await (await page.request.get(`/api/v2/sessions/${first}`, { headers })).json();
  const owner = firstView.agentInstanceId;
  const files = {
    "CsvTool.csproj": '<Project Sdk="Microsoft.NET.Sdk"/>\r\n',
    "CsvUtil.cs": "// Csv utility café\r\n",
    "Program.cs": 'Console.WriteLine("CSV");\n',
    "README.md": "# CsvTool\r\nExact project bytes.\n"
  };
  for (const [name, content] of Object.entries(files)) {
    const path = encodeURIComponent(`/home/csharp/CsvTool/${name}`);
    expect(await (await page.request.get(`/api/v2/sessions/${first}/workspace/content?path=${path}`, { headers })).body()).toEqual(Buffer.from(content));
  }
  const home = await (await page.request.get(`/api/v2/agent-instances/${owner}/workspace`, { headers })).json();
  expect(home.items.some((item: { logicalPath: string }) => item.logicalPath.startsWith("/home/c#"))).toBe(false);
  expect(home.items).toContainEqual(expect.objectContaining({ logicalPath: "/home/csharp/CsvTool/empty", directory: true, sourceSessionId: first }));
  const opened = await page.request.post("/api/v2/sessions", { headers, data: { agentInstanceId: owner, mode: "text" } });
  expect(opened.ok()).toBe(true);
  const fresh = (await opened.json()).sessionId;
  await page.goto(`/c/${fresh}`);
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 20_000 });
  expect((await page.request.get(`/api/v2/sessions/${fresh}/workspace/content?path=/working/csharp/CsvTool/README.md`, { headers })).status()).toBe(404);
  const edited = await send("project-edit");
  await expect(edited).toContainText('"cwd":"/home"');
  const pending = page.waitForEvent("download");
  await page.getByRole("button", { name: "Download README.md", exact: true }).click();
  const artifact = await pending;
  const updated = Buffer.from("# CsvTool\r\nVerified durable project.\n");
  expect(await readFile((await artifact.path())!)).toEqual(updated);
  await page.goto("/");
  expect((await page.request.delete(`/api/v2/sessions/${first}`, { headers })).ok()).toBe(true);
  const path = "/home/csharp/CsvTool/README.md";
  expect(await (await page.request.get(`/api/v2/sessions/${fresh}/workspace/content?path=${path}`, { headers })).body()).toEqual(updated);
  expect((await page.request.put(`/api/v2/sessions/${fresh}/workspace/content?path=${path}&expectedRevision=1`, { headers, data: "stale" })).status()).toBe(409);
  expect((await page.request.patch(`/api/v2/admin/agent-instances/${owner}/lifecycle`, { headers, data: { expectedRevision: 1, lifecycle: "Archived" } })).ok()).toBe(true);
  const archived = await (await page.request.get(`/api/v2/agent-instances/${owner}/workspace`, { headers })).json();
  expect(archived.items).toHaveLength(home.items.length);
  const nodes = await (await page.request.get(`/api/v2/sessions/${fresh}/workspace?prefix=/home/csharp/CsvTool`, { headers })).json();
  expect(nodes.every((node: { writable: boolean }) => !node.writable)).toBe(true);
  expect((await page.request.put(`/api/v2/sessions/${fresh}/workspace/content?path=/home/no.txt`, { headers, data: "blocked" })).status()).toBe(409);
  expect((await page.request.delete(`/api/v2/sessions/${fresh}`, { headers })).ok()).toBe(true);
  const removed = await page.request.delete(`/api/v2/admin/agent-instances/${owner}`, { headers, data: { expectedRevision: 2 } });
  expect(removed.status()).toBe(409);
  expect((await removed.json()).detail).toContain("AgentRuns");
  expect((await page.request.get(`/api/v2/agent-instances/${owner}/workspace`, { headers })).ok()).toBe(true);

  // AgentRun receipts deliberately prevent cascading instance deletion.
  // A separate unreferenced owner exercises the normal durable-home purge boundary.
  const provisioned = await page.request.post("/api/v2/admin/agent-instances", {
    headers, data: { definitionId: "general-assistant", version: 17 }
  });
  expect(provisioned.ok()).toBe(true);
  const disposable = (await provisioned.json()).instanceId;
  const session = (await (await page.request.post("/api/v2/sessions", {
    headers, data: { agentInstanceId: disposable, mode: "text" }
  })).json()).sessionId;
  expect((await page.request.put(`/api/v2/sessions/${session}/workspace/content?path=/home/purge.txt`, { headers, data: "owned bytes" })).ok()).toBe(true);
  expect((await page.request.delete(`/api/v2/sessions/${session}`, { headers })).ok()).toBe(true);
  expect((await page.request.patch(`/api/v2/admin/agent-instances/${disposable}/lifecycle`, {
    headers, data: { expectedRevision: 1, lifecycle: "Archived" }
  })).ok()).toBe(true);
  expect((await page.request.delete(`/api/v2/admin/agent-instances/${disposable}`, {
    headers, data: { expectedRevision: 2 }
  })).ok()).toBe(true);
  expect((await page.request.get(`/api/v2/agent-instances/${disposable}/workspace`, { headers })).status()).toBe(404);
});
