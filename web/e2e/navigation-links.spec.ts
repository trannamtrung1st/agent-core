import { expect, test, type Locator, type Page } from "@playwright/test";
import { isCanceledEmailAccountPoll } from "./support/request-failures";

async function openInNewTab(page: Page, link: Locator, gesture: "modifier" | "middle" = "modifier") {
  await page.bringToFront();
  const source = page.url();
  const href = await link.getAttribute("href");
  expect(href).toBeTruthy();
  let opened: Page | null = null;
  // Main CI observed no tab after the first native background-tab gesture.
  // Retry the actual gesture, bounded by a short popup wait, while requiring the
  // source to remain unchanged. Never replace it with scripted window.open.
  for (let attempt = 0; attempt < 2 && !opened; attempt += 1) {
    const popup = page.context().waitForEvent("page", { timeout: 5_000 }).catch(() => null);
    await link.click(gesture === "middle" ? { button: "middle" } : { modifiers: ["ControlOrMeta"] });
    opened = await popup;
    await expect(page).toHaveURL(source);
  }
  expect(opened, `Native ${gesture} activation of ${href} must open a tab`).not.toBeNull();
  if (!opened) throw new Error(`Native ${gesture} activation of ${href} did not open a tab`);
  await opened.bringToFront();
  await opened.waitForURL(new URL(href!, source).href, { waitUntil: "domcontentloaded", timeout: 15_000 });
  await expect(opened).toHaveURL(new URL(href!, source).href);
  await expect(page).toHaveURL(source);
  return opened;
}

for (const width of [1440, 390]) {
  test(`route links preserve the source and restore the destination at ${width}px`, async ({ page }) => {
    test.setTimeout(90_000);
    const errors: string[] = [];
    const failed: string[] = [];
    page.on("pageerror", error => errors.push(error.message));
    page.on("console", message => { if (message.type() === "error") errors.push(message.text()); });
    page.on("requestfailed", request => {
      if (!isCanceledEmailAccountPoll(request)) failed.push(`${request.method()} ${request.url()}`);
    });
    await page.setViewportSize({ width, height: 900 });
    await page.goto("/");
    await page.waitForFunction(() => localStorage.getItem("agent-core.owner-capability"));
    await expect(page.getByTestId("profile")).toHaveText("Synthetic");
    await expect(page.getByLabel("Message", { exact: true })).toBeVisible();
    const headers = { "X-AgentCore-Owner-Capability": (await page.evaluate(() => localStorage.getItem("agent-core.owner-capability")))! };
    const created = await page.request.post("/api/v2/admin/agent-instances", { headers, data: { definitionId: "examiner", version: 1 } });
    expect(created.ok()).toBe(true);
    const { instanceId } = await created.json();
    const base = `/admin/instances/${instanceId}`;
    const admin = await openInNewTab(page, page.getByRole("link", { name: "Open Admin", exact: true }), "middle");
    await expect(admin.getByRole("table", { name: "Definitions table", exact: true })).toBeVisible();
    await admin.close();
    await page.getByRole("link", { name: "Open Admin", exact: true }).click();
    await page.getByRole("tab", { name: "Instances", exact: true }).getByRole("link").click();
    const definitions = await openInNewTab(page, page.getByRole("tab", { name: "Definitions", exact: true }).getByRole("link"), "middle");
    await expect(definitions.getByRole("table", { name: "Definitions table", exact: true })).toBeVisible();
    await expect(page.getByRole("tab", { name: "Instances", exact: true })).toHaveAttribute("aria-selected", "true");
    await definitions.close();
    await page.getByRole("textbox", { name: "Search instances", exact: true }).fill(instanceId);
    const instance = page.locator(`a[href="${base}"]`);
    const detail = await openInNewTab(page, instance);
    await expect(detail.getByLabel("Persona name", { exact: true })).toHaveValue("Alex");
    await detail.close();
    await instance.click();
    await page.getByLabel("Persona name", { exact: true }).fill("Unsaved navigation edit");
    const automation = await openInNewTab(page, page.getByRole("tab", { name: "Automation", exact: true }).getByRole("link"));
    await expect(automation.getByRole("tab", { name: "Triggers", exact: true })).toHaveAttribute("aria-selected", "true");
    await expect(automation.getByText("No automations yet. Create one here or ask the agent in Chat to do something later.", { exact: true })).toBeVisible();
    await automation.close();
    await expect(page.getByLabel("Persona name", { exact: true })).toHaveValue("Unsaved navigation edit");
    await expect(page.getByRole("tab", { name: "Identity & version", exact: true })).toHaveAttribute("aria-selected", "true");
    await page.getByRole("tab", { name: "Skills & resources", exact: true }).getByRole("link").click();
    const resources = await openInNewTab(page, page.getByRole("tab", { name: "Resources", exact: true }).getByRole("link"), "middle");
    await expect(resources.getByRole("tab", { name: "Resources", exact: true })).toHaveAttribute("aria-selected", "true");
    await resources.close();
    await expect(page.getByRole("tab", { name: "Skills", exact: true })).toHaveAttribute("aria-selected", "true");
    await page.getByRole("tab", { name: "Identity & version", exact: true }).getByRole("link").click();
    await expect(page.getByLabel("Persona name", { exact: true })).toHaveValue("Unsaved navigation edit");
    // Keyboard activation stays on the AntD tab stop, including its arrow-key behavior.
    const identity = page.getByRole("tab", { name: "Identity & version", exact: true });
    await identity.focus();
    await page.keyboard.press("ArrowRight");
    await page.keyboard.press("Enter");
    await expect(page.getByRole("tab").filter({ has: page.getByRole("link", { name: "Skills & resources", exact: true }) })).toHaveAttribute("aria-selected", "true");
    await page.goBack();
    await expect(page.getByLabel("Persona name", { exact: true })).toHaveValue("Unsaved navigation edit");
    await page.getByRole("tab", { name: "Settings", exact: true }).getByRole("link").click();
    await page.reload();
    await expect(page.getByRole("tab", { name: "Settings", exact: true })).toHaveAttribute("aria-selected", "true");
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(width);
    expect(errors).toEqual([]);
    expect(failed).toEqual([]);
    await page.getByRole("tab", { name: "Profile", exact: true }).getByRole("link").click();
    await page.getByRole("button", { name: "Archive instance", exact: true }).click();
    await page.getByRole("dialog").getByRole("button", { name: "Archive", exact: true }).click();
    await expect(page.getByRole("button", { name: "Unarchive instance", exact: true })).toBeVisible();
    await page.goto("/admin/instances");
    const config = await (await page.request.get(`/api/v2/admin/instances/${instanceId}/effective-config`, { headers })).json();
    expect((await page.request.delete(`/api/v2/admin/agent-instances/${instanceId}`, { headers, data: { expectedRevision: config.instanceRevision } })).ok()).toBe(true);
  });
}
