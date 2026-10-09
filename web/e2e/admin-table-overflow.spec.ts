import { expect, test, type Locator } from "@playwright/test";

async function expectContainedText(control: Locator) {
  expect(await control.evaluate(element => {
    const cell = element.closest("td")!;
    const bounds = cell.getBoundingClientRect();
    const range = document.createRange();
    range.selectNodeContents(element);
    return [...range.getClientRects()].every(rect => rect.left >= bounds.left && rect.right <= bounds.right);
  })).toBe(true);
}

test("collection links wrap without overlapping adjacent cells at desktop and mobile widths", async ({ page }) => {
  const errors: string[] = [];
  page.on("pageerror", error => errors.push(error.message));
  page.on("response", response => { if (response.status() >= 500) errors.push(`${response.status()} ${response.url()}`); });
  await page.goto("/admin/instances");
  await page.waitForFunction(() => localStorage.getItem("agent-core.owner-capability"));
  const owner = (await page.evaluate(() => localStorage.getItem("agent-core.owner-capability")))!;
  const headers = { "X-AgentCore-Owner-Capability": owner };
  const created = await page.request.post("/api/v2/admin/agent-instances", { headers, data: { definitionId: "secretary", version: 5 } });
  expect(created.ok()).toBe(true);
  const instanceId = (await created.json()).instanceId;
  const path = `/api/v2/admin/agent-instances/${instanceId}`;
  const name = "Review pending orders and outstanding customer requests ".repeat(2).trim();
  const saved = await page.request.post(`${path}/automations`, { headers, data: {
    executionTarget: { kind: "backgroundSession" }, completionDelivery: { kind: "none" },
    expectedRevision: 0, enabled: true, name,
    instructions: "Review only. Do nothing when nothing needs action.",
    trigger: { kind: "schedule", schedule: { kind: "oneShot", timeZone: "UTC", atUtc: new Date(Date.now() + 2000).toISOString() } }
  } });
  expect(saved.ok(), await saved.text()).toBe(true);
  await expect.poll(async () => {
    const response = await page.request.get(`/api/v2/agent-instances/${instanceId}/agent-runs`, { headers });
    return (await response.json()).items[0]?.status;
  }, { timeout: 30000 }).toBe("completed");
  // Reproduce the longer attention label from the reported screenshot.
  await page.route(`**${path}/automations`, async route => {
    const response = await route.fetch();
    const data = await response.json();
    data.items[0].outcome = "NeedsAttention";
    data.items[0].effectiveModelKey = "deepseek-v41-flash";
    await route.fulfill({ response, json: data });
  });
  await page.goto(`/admin/instances/${instanceId}/automation`);
  const link = page.getByRole("button", { name: `View last run: ${name}`, exact: true });
  await expect(link).toHaveText("Completed · Needs attention");
  for (const width of [1440, 768, 390]) {
    await page.setViewportSize({ width, height: 900 });
    await link.scrollIntoViewIfNeeded();
    await expectContainedText(link);
    const summary = page.getByRole("button", { name: `View automation: ${name}`, exact: true });
    expect(await summary.evaluate(el => getComputedStyle(el).whiteSpace)).toBe("nowrap");
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  }
  await link.click();
  const details = page.getByRole("dialog", { name: "Run details", exact: true });
  await expect(details.getByText("No action", { exact: true }).first()).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(details).toBeHidden();
  await page.getByRole("tab", { name: "Activity", exact: true }).click();
  await page.getByRole("tab", { name: "Runs", exact: true }).click();
  const runs = page.getByRole("table", { name: "Runs table", exact: true });
  await expect(runs).toContainText("Scheduled task");
  const runName = runs.locator("tbody tr").first().locator("td").first();
  await expectContainedText(runName);
  await page.goto("/admin/definitions/examiner/versions");
  const version = page.getByRole("button", { name: "View v1 (builtIn)", exact: true });
  await expectContainedText(version);
  await version.click();
  await expect(page.getByRole("dialog")).toBeVisible();
  expect(errors).toEqual([]);
});

test("Skill and event-source action buttons stay together across table widths", async ({ page }) => {
  await page.goto("/admin/instances");
  await page.waitForFunction(() => localStorage.getItem("agent-core.owner-capability"));
  const owner = (await page.evaluate(() => localStorage.getItem("agent-core.owner-capability")))!;
  const headers = { "X-AgentCore-Owner-Capability": owner };
  const created = await page.request.post("/api/v2/admin/agent-instances", { headers, data: { definitionId: "general-assistant", version: 17 } });
  expect(created.ok()).toBe(true);
  const instanceId = (await created.json()).instanceId;
  await page.goto(`/admin/instances/${instanceId}/skills`);
  const skills = page.getByRole("region", { name: "Definition Skills", exact: true });
  const inspect = skills.getByRole("button", { name: "Inspect", exact: true }).first();
  await expect(inspect).toBeVisible();
  async function checkActions(cell: Locator) {
    for (const width of [1440, 768, 390]) {
      await page.setViewportSize({ width, height: 900 });
      await expect.poll(() => cell.evaluate(element => {
        const bounds = element.getBoundingClientRect();
        const buttons = [...element.querySelectorAll("button")].map(button => button.getBoundingClientRect());
        const center = buttons[0]?.y + buttons[0]?.height / 2;
        return buttons.length > 1 && buttons.every(rect => Math.abs(rect.y + rect.height / 2 - center) < 1 && rect.left >= bounds.left && rect.right <= bounds.right);
      })).toBe(true);
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    }
  }
  await checkActions(inspect.locator("xpath=ancestor::td"));
  await inspect.click();
  await expect(page.getByRole("dialog", { name: "Definition Skill details", exact: true })).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(page.getByRole("dialog", { name: "Definition Skill details", exact: true })).toBeHidden();
  await skills.getByRole("button", { name: "Customize", exact: true }).first().click();
  const confirmation = page.getByRole("dialog", { name: "Customize Definition Skill?", exact: true });
  await expect(confirmation).toContainText("independent Instance Skill");
  await confirmation.getByRole("button", { name: "Cancel", exact: true }).click();
  const sourceName = `Action row ${Date.now()}`;
  const source = await page.request.post("/api/v2/admin/connections/events", { headers, data: { displayName: sourceName, eventKey: `overflow.${Date.now()}` } });
  expect(source.ok()).toBe(true);
  await page.goto("/admin/connections/events");
  const copy = page.getByRole("button", { name: `More actions for ${sourceName}`, exact: true });
  await expect(copy).toBeVisible();
  await checkActions(copy.locator("xpath=ancestor::td"));
});
