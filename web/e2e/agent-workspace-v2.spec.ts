import { expect, test } from "@playwright/test";
import { readFile } from "node:fs/promises";

test("managed workspace uses durable cwd, explicit scratch, and downloads across sessions", async ({ page }) => {
  test.setTimeout(120_000);
  await page.goto("/");
  await expect.poll(() => page.evaluate(() => localStorage.getItem("agent-core.owner-capability"))).not.toBeNull();
  const token = await page.evaluate(() => localStorage.getItem("agent-core.owner-capability"));
  const headers = { "X-AgentCore-Owner-Capability": token! };
  const created = await page.request.post("/api/v2/admin/agent-instances", { headers, data: { definitionId: "general-assistant", version: 16 } });
  expect(created.ok(), await created.text()).toBe(true);
  const owner = (await created.json()).instanceId;
  async function session() {
    const opened = await page.request.post("/api/v2/sessions", { headers, data: { agentInstanceId: owner, mode: "text", modelCatalogKey: "scripted-alpha" } });
    expect(opened.ok(), await opened.text()).toBe(true);
    return (await opened.json()).sessionId as string;
  }
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
    await expect(page.locator(".chat-message-assistant").last()).toContainText("Agent Workspace v2 results:", { timeout: 45_000 });
    await expect(page.getByRole("button", { name: "Send", exact: true })).toBeVisible();
  }
  const first = await session();
  await page.goto(`/c/${first}`); await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 20_000 });
  await send("setup");
  const answer = page.locator(".chat-message-assistant").last();
  await expect(answer).toContainText('"cwd":"/home"');
  await expect(answer).toContainText('"cwd":"/home/projects/customer-a"');
  await expect(answer).toContainText('"cwd":"/working"');
  await expect(answer).not.toContainText('"error":');
  await expect(answer).not.toContainText("/workspace/working");
  const expected = Buffer.from("Durable result café\r\n");
  expect(await (await page.request.get(`/api/v2/sessions/${first}/workspace/content?path=/home/projects/customer-a/result.md`, { headers })).body()).toEqual(expected);
  const downloadEvent = page.waitForEvent("download");
  await page.getByRole("button", { name: "Download result.md", exact: true }).last().click();
  const downloaded = await downloadEvent;
  expect(downloaded.suggestedFilename()).toBe("result.md");
  expect(await readFile((await downloaded.path())!)).toEqual(expected);
  await send("cwd"); await expect(page.locator(".chat-message-assistant").last()).toContainText('"cwd":"/working"');
  await page.goto("/");
  expect((await page.request.delete(`/api/v2/sessions/${first}`, { headers })).ok()).toBe(true);
  const second = await session();
  expect((await page.request.get(`/api/v2/sessions/${second}/workspace/content?path=/working/result.md`, { headers })).status()).toBe(404);
  await page.goto(`/c/${second}`); await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 20_000 });
  await send("restore"); await expect(page.locator(".chat-message-assistant").last()).toContainText('"cwd":"/home"');
  await expect(page.locator(".chat-message-assistant").last()).toContainText("Durable result caf");
  await page.goto(`/admin/instances/${owner}/workspace`);
  await expect(page.getByRole("heading", { name: "Agent Workspace", exact: true })).toBeVisible();
  await expect(page.getByText("/home · Durable across sessions.", { exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Download /home/projects/customer-a", exact: true })).toBeDisabled();
  for (const width of [1440, 768, 390]) {
    await page.setViewportSize({ width, height: 900 });
    await expect(page.getByText("/home/projects/customer-a/result.md", { exact: true })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
  }
});
