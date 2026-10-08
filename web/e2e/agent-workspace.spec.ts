import { expect, test } from "@playwright/test";
import { readFile } from "node:fs/promises";
import { createHash } from "node:crypto";

test("managed home survives deleted source, guards a revision and delivers a fresh Artifact", async ({ page }, testInfo) => {
  test.setTimeout(120_000);
  await page.goto("/");
  await expect.poll(() => page.evaluate(() => localStorage.getItem("agent-core.owner-capability"))).not.toBeNull();
  const token = await page.evaluate(() => localStorage.getItem("agent-core.owner-capability"));
  const headers = { "X-AgentCore-Owner-Capability": token! };
  const request = page.request;
  async function instance() {
    const response = await request.post("/api/v2/admin/agent-instances", { headers, data: { definitionId: "general-assistant", version: 17 } });
    expect(response.ok(), await response.text()).toBe(true); return (await response.json()).instanceId as string;
  }
  async function session(id: string) {
    const response = await request.post("/api/v2/sessions", { headers, data: { agentInstanceId: id, mode: "text", modelCatalogKey: "scripted-alpha" } });
    expect(response.ok(), await response.text()).toBe(true); return (await response.json()).sessionId as string;
  }
  const a = await instance(); const b = await instance(); const a1 = await session(a);
  const scratch = "/working/store-review.md"; const home = "/home/reports/store-review.md";
  const source = Buffer.from("# Store review\r\nExact source: café\n");
  expect((await request.put(`/api/v2/sessions/${a1}/workspace/content?path=${scratch}`, { headers, data: source })).ok()).toBe(true);
  expect((await request.put(`/api/v2/sessions/${a1}/workspace/content?path=${home}`, { headers, data: source })).ok()).toBe(true);
  const first = (await (await request.get(`/api/v2/agent-instances/${a}/workspace`, { headers })).json()).items.find((item: { logicalPath: string }) => item.logicalPath === home);
  expect(first.sha256Hex).toBe(createHash("sha256").update(source).digest("hex"));
  expect((await request.delete(`/api/v1/sessions/${a1}`, { headers })).ok()).toBe(true);
  expect((await request.delete(`/api/v2/sessions/${a1}`, { headers })).ok()).toBe(true);
  expect((await request.get(`/api/v2/sessions/${a1}/workspace/content?path=${scratch}`, { headers })).status()).toBe(404);
  const a2 = await session(a);
  await page.goto(`/c/${a2}`); await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 20_000 });
  await expect(async () => {
    await page.getByLabel("Message").fill("synthetic-agent-workspace: search");
    await expect(page.getByRole("button", { name: "Send", exact: true })).toBeEnabled();
  }).toPass(); await page.getByRole("button", { name: "Send", exact: true }).click();
  await expect(page.locator(".chat-message-assistant").last()).toContainText(home, { timeout: 30_000 });
  const read = await request.get(`/api/v2/sessions/${a2}/workspace/content?path=${home}`, { headers }); expect(await read.body()).toEqual(source);
  const revised = Buffer.from("# Revised store review\nKept exact bytes across conversations.\n");
  const replacement = await request.put(`/api/v2/sessions/${a2}/workspace/content?path=${home}&expectedRevision=${first.revision}`, { headers, data: revised });
  expect(replacement.ok(), await replacement.text()).toBe(true);
  const second = (await (await request.get(`/api/v2/agent-instances/${a}/workspace`, { headers })).json()).items.find((item: { logicalPath: string }) => item.logicalPath === home);
  expect(second.revision).toBe(2);
  expect((await request.put(`/api/v2/sessions/${a2}/workspace/content?path=${home}&expectedRevision=${first.revision}`, { headers, data: source })).status()).toBe(409);
  await page.getByLabel("Message").fill("synthetic-agent-workspace: publish"); await page.getByRole("button", { name: "Send", exact: true }).click();
  const card = page.getByRole("group", { name: "store-review.md", exact: true }); await expect(card).toBeVisible({ timeout: 30_000 });
  await expect(card).toContainText(`${revised.length} B`);
  const pendingArtifact = page.waitForEvent("download"); await page.getByRole("button", { name: "Download store-review.md", exact: true }).click();
  const artifact = await pendingArtifact; expect(await readFile((await artifact.path())!)).toEqual(revised);
  const records = await (await request.get(`/api/v2/sessions/${a2}/artifacts`, { headers })).json();
  expect(records[0].artifactId).not.toBe(second.itemId); expect(records[0].sessionId).toBe(a2);
  await page.goto(`/admin/instances/${a}/identity`); await page.getByRole("tab", { name: "Workspace", exact: true }).click();
  await expect(page.getByText(home, { exact: true })).toBeVisible();
  const pendingHome = page.waitForEvent("download"); await page.getByRole("button", { name: `Download ${home}`, exact: true }).click();
  const durable = await pendingHome; expect(await readFile((await durable.path())!)).toEqual(revised);
  for (const width of [1440, 768, 390]) {
    await page.setViewportSize({ width, height: 900 });
    await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    await page.screenshot({ path: testInfo.outputPath(`home-${width}.png`), fullPage: true });
  }
  await page.goto(`/admin/instances/${b}/identity`); await page.getByRole("tab", { name: "Workspace", exact: true }).click(); await expect(page.getByText("No workspace items yet")).toBeVisible();
  expect((await request.get(`/api/v2/agent-instances/${b}/workspace/${second.itemId}/content`, { headers })).status()).toBe(404);
  await page.goto(`/admin/instances/${a}/identity`); await page.getByRole("tab", { name: "Workspace", exact: true }).click(); await expect(page.getByText(home, { exact: true })).toBeVisible();
  await page.getByRole("button", { name: `Delete ${home}`, exact: true }).click();
  await page.getByRole("button", { name: "Cancel", exact: true }).click(); await expect(page.getByText(home, { exact: true })).toBeVisible();
  // The separate SQLite reopen/Compose gate proves restart survival; keep this UI journey fast and deterministic.
  expect((await request.patch(`/api/v2/admin/agent-instances/${a}/lifecycle`, { headers, data: { expectedRevision: 1, lifecycle: "Archived" } })).ok()).toBe(true);
  await page.reload(); await expect(page.getByText("Archived workspace is read-only")).toBeVisible();
  await expect(page.getByRole("button", { name: `Delete ${home}`, exact: true })).toBeDisabled();
  expect(await (await request.get(`/api/v2/agent-instances/${a}/workspace/${second.itemId}/content`, { headers })).body()).toEqual(revised);
});
