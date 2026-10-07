import { expect, test, type Page } from "@playwright/test";
import { readFile } from "node:fs/promises";
import { INSTANCE_DEFINITIONS, selectInstanceIdentity } from "./support/instance-identity";

async function downloadBytes(page: Page, filename: string): Promise<Buffer> {
  const pending = page.waitForEvent("download");
  await page.getByRole("button", { name: `Download ${filename}`, exact: true }).focus();
  await page.keyboard.press("Enter");
  const download = await pending;
  expect(download.suggestedFilename()).toBe(filename);
  const path = await download.path();
  expect(path).not.toBeNull();
  return readFile(path!);
}

test("published real-GUID artifact downloads exact bytes through reload, reconnect and ended history", async ({ page }) => {
  test.setTimeout(90_000);
  const contentRequests: string[] = [];
  page.on("request", request => {
    if (/\/artifacts\/[^/]+\/content$/.test(request.url())) {
      contentRequests.push(request.url());
      expect(request.headers()["x-agentcore-owner-capability"]).toBeTruthy();
    }
  });
  await page.goto("/");
  await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.customerSupport);
  await page.getByRole("button", { name: "Model", exact: true }).click();
  await page.getByTitle("Scripted Alpha", { exact: true }).click();
  await page.getByLabel("Message").fill("Artifact delivery proof for order 91.");
  await page.getByRole("button", { name: "Send", exact: true }).click();
  const card = page.getByRole("group", { name: "case-note.md", exact: true });
  await expect(card).toBeVisible({ timeout: 30_000 });
  await expect(card).toContainText("Markdown");
  await expect(page.locator(".chat-message-assistant")).not.toContainText("[[artifact:");
  expect(contentRequests).toHaveLength(0);
  const sessionId = page.url().split("/c/")[1];
  const token = await page.evaluate(() => localStorage.getItem("agent-core.owner-capability"));
  const headers = { "X-AgentCore-Owner-Capability": token! };
  const records = await (await page.request.get(`/api/v2/sessions/${sessionId}/artifacts`, { headers })).json();
  expect(records).toHaveLength(1);
  expect(records[0].artifactId).toMatch(/^[0-9a-f]{8}-[0-9a-f-]{27}$/i);
  expect(records[0].displayName).toBe("case-note.md");
  const expected = Buffer.from("Demo-only order status language. Cite this document as support-order-policy@demo. Do not invent refunds or live account changes");
  expect(records[0].byteSize).toBe(expected.length);
  await expect(card).toContainText(`${expected.length} B`);
  expect(await downloadBytes(page, "case-note.md")).toEqual(expected);
  expect(contentRequests).toHaveLength(1);
  await page.evaluate(async () => {
    await window.__agentCore?.disconnect();
    await window.__agentCore?.reconnect?.();
  });
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 20_000 });
  await expect(card).toBeVisible();
  await page.reload();
  await expect(card).toBeVisible({ timeout: 20_000 });
  expect(contentRequests).toHaveLength(1);
  expect(await downloadBytes(page, "case-note.md")).toEqual(expected);
  const ended = await page.request.delete(`/api/v1/sessions/${sessionId}`, { headers });
  expect(ended.ok()).toBe(true);
  await page.reload();
  await expect(page.getByLabel("Message")).toHaveCount(0);
  await expect(card).toBeVisible();
  expect(await downloadBytes(page, "case-note.md")).toEqual(expected);
  for (const width of [1440, 768, 390]) {
    await page.setViewportSize({ width, height: 900 });
    await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    await expect(page.getByRole("button", { name: "Download case-note.md", exact: true })).toBeVisible();
  }
});

test("artifact errors retry locally and long filenames remain accessible at all widths", async ({ page }, testInfo) => {
  test.setTimeout(90_000);
  const longName = "quarterly-review-".repeat(16) + ".md";
  let metadataReads = 0;
  // UI stress fixture only; the first test above proves unmodified real Core metadata/bytes.
  await page.route(/\/artifacts\/[^/]+$/, async route => {
    if (++metadataReads === 1) {
      await route.fulfill({ status: 503, body: "temporary metadata failure" });
    } else {
      const response = await route.fetch();
      await route.fulfill({ response, json: { ...await response.json(), displayName: longName } });
    }
  });
  await page.goto("/");
  await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.customerSupport);
  await page.getByLabel("Message").fill("Artifact retry proof for order 91.");
  await page.getByRole("button", { name: "Send", exact: true }).click();
  await expect(page.getByText("File unavailable", { exact: true })).toBeVisible({ timeout: 30_000 });
  await expect(page.locator(".chat-message-assistant")).toContainText("Order 91 is delayed.");
  await page.getByRole("button", { name: "Retry file", exact: true }).click();
  const button = page.getByRole("button", { name: `Download ${longName}`, exact: true });
  await expect(button).toBeVisible();
  await expect(page.locator(".artifact-filename")).toHaveAttribute("title", longName);
  await page.route("**/artifacts/*/content", async route => {
    await route.fulfill({ status: 503, body: "temporary download failure" });
    await page.unroute("**/artifacts/*/content");
  });
  await button.click();
  await expect(page.getByText("Download failed. Try again.", { exact: true })).toBeVisible();
  for (const width of [1440, 768, 390]) {
    await page.setViewportSize({ width, height: 900 });
    await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await expect(page.getByRole("button", { name: `Retry download ${longName}`, exact: true })).toBeVisible();
  }
  const pending = page.waitForEvent("download");
  await page.getByRole("button", { name: `Retry download ${longName}`, exact: true }).focus();
  await page.keyboard.press("Enter");
  expect((await pending).suggestedFilename()).toBe(longName);
  await page.getByLabel("Message").fill("Another Artifact retry proof for order 91.");
  await page.getByRole("button", { name: "Send", exact: true }).click();
  await expect(page.locator(".artifact-card")).toHaveCount(2, { timeout: 30_000 });
  for (const width of [1440, 768, 390]) {
    await page.setViewportSize({ width, height: 900 });
    await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await expect(page.getByRole("button", { name: `Download ${longName}`, exact: true })).toHaveCount(2);
    await page.screenshot({ path: testInfo.outputPath(`artifact-${width}.png`), fullPage: true });
  }
});
