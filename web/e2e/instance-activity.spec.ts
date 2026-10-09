import { expect, test, type Page } from "@playwright/test";
async function setup(page: Page, definitionId = "examiner", version = 1) {
  await page.goto("/admin/instances");
  await page.waitForFunction(() => localStorage.getItem("agent-core.owner-capability"));
  const headers = { "X-AgentCore-Owner-Capability": (await page.evaluate(() => localStorage.getItem("agent-core.owner-capability")))! };
  const response = await page.request.post("/api/v2/admin/agent-instances", { headers, data: { definitionId, version } });
  expect(response.ok(), await response.text()).toBe(true);
  return { headers, instanceId: (await response.json()).instanceId as string };
}
async function create(page: Page, headers: Record<string, string>, instanceId: string, title: string) {
  const response = await page.request.post("/api/v2/sessions", { headers, data: { agentInstanceId: instanceId, mode: "text" } });
  expect(response.ok()).toBe(true);
  const sessionId = (await response.json()).sessionId as string;
  expect((await page.request.post(`/api/v2/sessions/${sessionId}/rename`, { headers, data: { title } })).ok()).toBe(true);
  return sessionId;
}
test("Activity scopes and pages sessions, preserves multi-turn diagnostics and return navigation", async ({ page }) => {
  test.setTimeout(120000);
  const errors: string[] = []; page.on("pageerror", error => errors.push(error.message));
  const { headers, instanceId } = await setup(page);
  const foreign = await setup(page);
  await create(page, headers, foreign.instanceId, "Foreign conversation");
  const sessionId = await create(page, headers, instanceId, "Oldest conversation");
  for (let i = 0; i < 21; i++) await create(page, headers, instanceId, `Empty work ${i}`);
  const path = `/admin/instances/${instanceId}/activity`;
  await page.goto(path);
  await expect(page.getByRole("tab", { name: "Sessions", exact: true })).toHaveAttribute("aria-selected", "true");
  const table = page.getByRole("table", { name: "Activity sessions table" });
  await expect(table.locator("tbody .admin-collection-name")).toHaveCount(20);
  await expect(table).not.toContainText("Foreign conversation");
  await page.getByRole("button", { name: "Load more", exact: true }).click();
  await expect(table.locator("tbody .admin-collection-name")).toHaveCount(22);
  const search = page.getByRole("textbox", { name: "Search loaded sessions" });
  await search.fill("no-match"); await expect(table).toContainText("No matching loaded sessions");
  await search.fill("Oldest"); await page.getByRole("button", { name: "Oldest conversation", exact: true }).click();
  await page.reload();
  const drawer = page.getByRole("dialog", { name: "Oldest conversation", exact: true });
  await expect(drawer).toContainText("No runs yet");
  await drawer.getByRole("button", { name: "Open conversation", exact: true }).click();
  await expect(page.getByTestId("connection")).toHaveText("Ready");
  for (const [i, message] of ["First Activity turn", "Second Activity turn"].entries()) {
    await page.getByLabel("Message", { exact: true }).fill(message); await page.getByRole("button", { name: "Send", exact: true }).click();
    await expect.poll(async () => (await (await page.request.get(`/api/v2/sessions/${sessionId}/agent-runs`, { headers })).json()).items.filter((run: { status: string }) => run.status === "completed").length).toBe(i + 1);
  }
  await expect(page.locator(".conversation-scroll")).toContainText("Hello from synthetic.");
  await page.getByRole("button", { name: "Back to Activity", exact: true }).click();
  await expect(search).toHaveValue("Oldest");
  await expect(table.getByRole("button", { name: "Oldest conversation", exact: true })).toHaveCount(1);
  await page.getByRole("tab", { name: "Runs", exact: true }).click();
  const runs = page.getByRole("table", { name: "Runs table" });
  await expect(runs.getByRole("button", { name: "Chat turn", exact: true })).toHaveCount(2);
  await runs.getByRole("button", { name: "Chat turn", exact: true }).first().click(); await page.reload();
  await expect(page.getByRole("dialog", { name: "Run details", exact: true })).toContainText("Hello from synthetic.");
  await page.keyboard.press("Escape"); await expect(page.getByRole("dialog", { name: "Run details", exact: true })).toBeHidden();
  await page.goBack(); await expect(page.getByRole("tab", { name: "Runs", exact: true })).toHaveAttribute("aria-selected", "true");
  await page.goto(`/admin/instances/${instanceId}/runs`);
  await expect(page.getByRole("tab", { name: "Activity", exact: true })).toHaveAttribute("aria-selected", "true");
  await expect(page.getByRole("tab", { name: "Runs", exact: true })).toHaveAttribute("aria-selected", "true");
  await page.getByRole("tab", { name: "Continuity", exact: true }).click();
  await expect(page.getByRole("tab", { name: "Memory", exact: true })).toBeVisible(); await expect(page.getByRole("tab", { name: "Experience", exact: true })).toBeVisible();
  expect(errors).toEqual([]);
});
test("Activity preserves the original background result and continues its exact Session", async ({ page }) => {
  test.setTimeout(120000);
  const { headers, instanceId } = await setup(page, "general-assistant", 21);
  const parentId = await create(page, headers, instanceId, "Parent conversation");
  await page.goto(`/c/${parentId}`); await expect(page.getByTestId("connection")).toHaveText("Ready");
  await page.getByLabel("Message", { exact: true }).fill("[test:background-start] [test:background-files] Check progress.");
  await page.getByRole("button", { name: "Send", exact: true }).click();
  await expect(page.locator(".conversation-scroll")).toContainText("Background work: Result A:", { timeout: 30000 });
  const child = (await (await page.request.get(`/api/v2/agent-instances/${instanceId}/background-sessions`, { headers })).json()).items[0];
  let unavailable = true;
  await page.route(`**/api/v2/sessions/${child.session.sessionId}/background`, route => unavailable
    ? route.fulfill({ status: 503, contentType: "application/problem+json", body: JSON.stringify({ detail: "Original result is temporarily unavailable" }) }) : route.continue());
  await page.goto(`/admin/instances/${instanceId}/activity/sessions?session=${child.session.sessionId}`);
  await expect(page.getByRole("alert").filter({ hasText: "Original result is temporarily unavailable" })).toHaveCount(1);
  await expect(page.getByLabel("Loading background Session", { exact: true })).toBeHidden();
  unavailable = false;
  await page.getByRole("button", { name: "Retry background session", exact: true }).click();
  const drawer = page.getByRole("dialog", { name: "Background progress check", exact: true });
  await expect(drawer).toContainText("Result A:"); await expect(drawer).toContainText("Original task files");
  unavailable = true;
  await expect(page.getByText("Original result is temporarily unavailable", { exact: true })).toBeVisible({ timeout: 10000 });
  await expect(page.getByRole("button", { name: "Continue in chat", exact: true })).toBeHidden();
  await expect(page.getByLabel("Loading background Session", { exact: true })).toBeHidden();
  unavailable = false; await page.getByRole("button", { name: "Retry background session", exact: true }).click();
  await expect(drawer).toContainText("Result A:");
  await drawer.getByRole("button", { name: "Continue in chat", exact: true }).click();
  await expect(page).toHaveURL(new RegExp(`/c/${child.session.sessionId}\\?returnTo=`));
  await expect(page.getByTestId("connection")).toHaveText("Ready"); await expect(page.locator(".conversation-scroll")).toContainText("Result A:");
  await page.getByRole("button", { name: "Back to Activity", exact: true }).click();
  await expect(page.getByRole("table", { name: "Activity sessions table" }).getByRole("button", { name: "Background progress check", exact: true })).toHaveCount(1);
});
test("Activity recovers a failed read and contains long titles at wide and narrow widths", async ({ page }) => {
  const { headers, instanceId } = await setup(page);
  await create(page, headers, instanceId, "Review pending customer requests and outstanding operations before preparing the weekly report with completed evidence");
  let fail = true;
  await page.route(`**/api/v2/agent-instances/${instanceId}/sessions?*`, route => fail ? route.fulfill({ status: 503, contentType: "application/problem+json", body: JSON.stringify({ detail: "Activity is temporarily unavailable" }) }) : route.continue());
  await page.goto(`/admin/instances/${instanceId}/activity`); await expect(page.getByText("Activity is temporarily unavailable", { exact: true })).toBeVisible();
  fail = false; await page.getByRole("button", { name: "Try again", exact: true }).click();
  const link = page.getByRole("table", { name: "Activity sessions table" }).locator("tbody .admin-collection-name");
  await expect(link).toHaveCount(1);
  for (const width of [1440, 768, 390]) {
    await page.setViewportSize({ width, height: 900 }); expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    expect(await link.evaluate(el => { const cell = el.closest("td")!.getBoundingClientRect(); const bounds = el.getBoundingClientRect(); return bounds.left >= cell.left && bounds.right <= cell.right; })).toBe(true);
  }
  const sessionId = (await (await page.request.get(`/api/v2/agent-instances/${instanceId}/sessions`, { headers })).json()).items[0].session.sessionId;
  let detailUnavailable = true;
  await page.route(`**/api/v2/agent-instances/${instanceId}/sessions/${sessionId}`, route => detailUnavailable
    ? route.fulfill({ status: 503, contentType: "application/problem+json", body: JSON.stringify({ detail: "Session details are temporarily unavailable" }) }) : route.continue());
  await link.click();
  await expect(page.getByText("Session details are temporarily unavailable", { exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Open conversation", exact: true })).toBeHidden();
  detailUnavailable = false; await page.getByRole("button", { name: "Retry session", exact: true }).click();
  await expect(page.getByRole("button", { name: "Open conversation", exact: true })).toBeVisible();
  await page.keyboard.press("Escape"); await expect(link).toBeFocused();
});
