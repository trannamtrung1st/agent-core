import { expect, test, type Page } from "@playwright/test";

async function policy(page: Page, body?: Record<string, unknown>) {
  return page.evaluate(async body => {
    const headers = { "content-type": "application/json", "X-AgentCore-Owner-Capability": localStorage.getItem("agent-core.owner-capability") ?? "" };
    const response = await fetch("/api/v2/admin/browser/privacy", body ? { method: "PUT", headers, body: JSON.stringify(body) } : { headers });
    if (!response.ok) throw new Error("privacy request " + response.status);
    return response.json();
  }, body);
}
async function openPrivacy(page: Page) {
  await page.goto("/");
  expect((await (await page.request.get("/health")).json()).profile).toBe("Synthetic");
  await expect.poll(() => page.evaluate(() => localStorage.getItem("agent-core.owner-capability"))).not.toBeNull();
  const token = await page.evaluate(() => localStorage.getItem("agent-core.owner-capability"));
  const instance = await page.request.post("/api/v2/admin/agent-instances", {
    headers: { "X-AgentCore-Owner-Capability": token! }, data: { definitionId: "approval-demo", version: 1 }
  });
  expect(instance.ok()).toBe(true);
  await page.goto(`/admin/instances/${(await instance.json()).instanceId}/effective`);
  const region = page.getByRole("region", { name: "Screenshot privacy", exact: true });
  await expect(region.getByRole("button", { name: /Save screenshot privacy$/ })).toBeVisible();
  return region;
}
async function selectMode(page: Page, name: string) {
  await page.getByLabel("Saved screenshot privacy mode").click();
  await page.locator(".ant-select-item-option").filter({ hasText: name }).click();
}

test("owner acknowledgement, saved/effective, conflict retained edits and retry", async ({ page }) => {
  const region = await openPrivacy(page);
  expect((await page.request.get("/api/v2/admin/browser/privacy")).status()).toBe(401);
  const before = await policy(page);
  expect(before.deployment.unmaskedAllowed).toBe(true);
  await selectMode(page, "Unmasked — explicit confidentiality exception");
  await region.getByLabel("Trusted exact origins for Unmasked capture").click();
  // Retain an already-selected fixture origin, or select it once.
  const option = page.locator(".ant-select-item-option").filter({ hasText: "https://example.test" });
  if (!(await option.getAttribute("class"))?.includes("ant-select-item-option-selected")) await option.click();
  await page.keyboard.press("Escape");
  await region.getByRole("button", { name: /Save screenshot privacy$/ }).click();
  const modal = page.getByRole("dialog", { name: "Authorize unmasked screenshots?" });
  await expect(modal).toContainText("credentials, OTPs");
  await expect.poll(() => page.evaluate(() => !!document.activeElement?.closest('[role="dialog"]'))).toBe(true);
  await modal.getByRole("button", { name: "Cancel", exact: true }).click();
  expect((await policy(page)).saved.revision).toBe(before.saved.revision);
  await region.getByRole("button", { name: /Save screenshot privacy$/ }).click();
  await modal.getByRole("button", { name: "Acknowledge exposure and save" }).click();
  await expect(region).toContainText(`Unmasked · revision ${before.saved.revision + 1} · restart required`);
  expect((await policy(page)).effective).toEqual(before.effective);
  await expect(modal).toBeHidden();
  await expect(region.locator("button.ant-btn-loading")).toHaveCount(0);
  await selectMode(page, "Disabled — no screenshots");
  const current = await policy(page);
  await policy(page, { expectedRevision: current.saved.revision, mode: "Protected", unmaskedOrigins: [], trustedGraphicsOrigins: [], acknowledgeExposure: false });
  await region.getByRole("button", { name: /Save screenshot privacy$/ }).click();
  await expect(region).toContainText("Browser privacy changed");
  await expect(region.getByLabel("Saved screenshot privacy mode").locator("xpath=.."))
    .toContainText("Disabled — no screenshots");
  await region.getByRole("button", { name: "Reload saved privacy policy" }).click();
  await expect(region).toContainText(`Protected · revision ${current.saved.revision + 1} · restart required`);
  await selectMode(page, "Disabled — no screenshots");
  await region.getByRole("button", { name: /Save screenshot privacy$/ }).click();
  await expect(region).toContainText(`Disabled · revision ${current.saved.revision + 2} · restart required`);
});

test("read failure retries through the same editor", async ({ page }) => {
  await page.route("**/api/v2/admin/browser/privacy", route => route.fulfill({ status: 503, contentType: "application/json", body: JSON.stringify({ detail: "Privacy read temporarily unavailable" }) }));
  await page.goto("/");
  await expect.poll(() => page.evaluate(() => localStorage.getItem("agent-core.owner-capability"))).not.toBeNull();
  const token = await page.evaluate(() => localStorage.getItem("agent-core.owner-capability"));
  const created = await page.request.post("/api/v2/admin/agent-instances", { headers: { "X-AgentCore-Owner-Capability": token! }, data: { definitionId: "approval-demo", version: 1 } });
  await page.goto(`/admin/instances/${(await created.json()).instanceId}/effective`);
  const region = page.getByRole("region", { name: "Screenshot privacy", exact: true });
  await expect(region.getByRole("button", { name: "Reload saved privacy policy" })).toBeVisible();
  await page.unroute("**/api/v2/admin/browser/privacy");
  await region.getByRole("button", { name: "Reload saved privacy policy" }).click();
  await expect(region.getByLabel("Saved screenshot privacy mode")).toBeVisible();
});

for (const width of [1440, 768, 390]) test(`privacy controls wrap and remain keyboard accessible at ${width}px`, async ({ page }) => {
  await page.setViewportSize({ width, height: 1000 });
  const region = await openPrivacy(page);
  await region.scrollIntoViewIfNeeded();
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  const bounds = (await region.boundingBox())!;
  expect(bounds.x).toBeGreaterThanOrEqual(0); expect(bounds.x + bounds.width).toBeLessThanOrEqual(width);
  await region.getByLabel("Saved screenshot privacy mode").focus();
  await page.keyboard.press("ArrowDown"); await page.keyboard.press("Escape");
  await expect(region.getByLabel("Saved screenshot privacy mode")).toBeFocused();
  await page.screenshot({ path: test.info().outputPath(`privacy-${width}.png`), fullPage: true });
});
