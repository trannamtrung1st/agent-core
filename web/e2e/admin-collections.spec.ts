import { expect, test } from "@playwright/test";
import { completeDefinitionDraftPublishGate } from "./admin-definition-gate-helpers";
import { draftEditorSection, forkBuiltInV1Draft } from "./admin-draft-editor-helpers";

test("Definition tabs keep draft creation compact and return to the draft list", async ({ page }) => {
  await page.goto("/");
  await page.waitForFunction(() => localStorage.getItem("agent-core.owner-capability"));
  await page.goto("/admin/definitions/examiner");
  await expect(page.getByRole("tab", { name: "Versions", exact: true })).toHaveAttribute("aria-selected", "true");
  await page.getByRole("tab", { name: "Drafts", exact: true }).click();
  await expect(page.getByRole("region", { name: "Definition versions", exact: true })).toBeHidden();
  for (const width of [1440, 390]) {
    await page.setViewportSize({ width, height: 900 });
    const selector = page.locator(".admin-draft-version-select");
    const fork = page.getByRole("button", { name: /Fork v/ });
    await expect(selector).toBeVisible();
    await expect(selector.getByRole("combobox")).toBeEnabled();
    await expect(fork).toBeVisible();
    await expect(fork).toBeEnabled();
    const select = await selector.boundingBox();
    const button = await fork.boundingBox();
    expect(select!.width).toBeLessThanOrEqual(320);
    if (width === 1440) {
      expect(Math.abs(button!.y - select!.y)).toBeLessThan(1);
      expect(Math.abs(button!.x - select!.x - select!.width - 8)).toBeLessThan(1);
    } else {
      expect(Math.abs(button!.y - select!.y - select!.height - 8)).toBeLessThan(1);
      expect(Math.abs(button!.x - select!.x)).toBeLessThan(1);
      expect(button!.height).toBeGreaterThanOrEqual(40);
    }
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(width);
  }
  await forkBuiltInV1Draft(page);
  await page.getByRole("button", { name: "Back to drafts", exact: true }).click();
  await expect(page.getByRole("tab", { name: "Drafts", exact: true })).toHaveAttribute("aria-selected", "true");
  await expect(page.getByRole("table", { name: "Definition drafts table", exact: true })).toBeVisible();
  await page.getByRole("tab", { name: "Versions", exact: true }).click();
  await page.getByRole("button", { name: "View v1 (builtIn)", exact: true }).click();
  await expect(page.getByRole("dialog", { name: "Version details", exact: true })).toBeVisible();
});

test("Admin collections paginate, sort, filter and recover from an empty search", async ({ page }) => {
  test.setTimeout(90_000);
  await page.goto("/");
  await page.waitForFunction(() => localStorage.getItem("agent-core.owner-capability"));
  const token = await page.evaluate(() => localStorage.getItem("agent-core.owner-capability"));
  const headers = { "X-AgentCore-Owner-Capability": token! };
  const prefix = `collection-${Date.now()}`;
  for (let index = 0; index < 12; index++) {
    const name = `${prefix}-${String(index).padStart(2, "0")}`;
    for (const [url, data] of [
      ["/api/v2/admin/definition-drafts/new", { definitionId: name }],
      ["/api/v2/admin/event-sources", { displayName: name }],
      ["/api/v2/admin/agent-instances", { definitionId: "examiner", version: 1,
        persona: { name, role: "Inspector", description: "A disposable collection fixture.", tone: "Clear" } }]
    ] as const) {
      const response = await page.request.post(url, { headers, data });
      expect(response.ok()).toBeTruthy();
    }
  }
  await page.goto("/admin");
  for (const [sectionName, searchName] of [
    ["Definitions", "Search definitions"], ["Instances", "Search instances"], ["Event sources", "Search event sources"]
  ]) {
    await page.getByRole("tab", { name: sectionName, exact: true }).click();
    const section = page.getByRole("region", { name: sectionName, exact: true });
    await section.getByLabel(searchName).fill(prefix);
    await expect(section.getByText("12 results", { exact: true })).toBeVisible();
    await expect(section.locator("tbody tr[data-row-key]")).toHaveCount(10);
    await section.locator(".ant-pagination-item-2").click();
    await expect(section.locator("tbody tr[data-row-key]")).toHaveCount(2);
    await section.getByLabel(searchName).fill(`${prefix}-11`);
    await expect(section.locator("tbody tr[data-row-key]")).toHaveCount(1);
    await section.getByLabel(searchName).fill("no-matching-collection");
    await expect(section.getByText(/No matches/)).toBeVisible();
    await section.getByLabel(searchName).fill(prefix);
    await expect(section.locator("tbody tr[data-row-key]")).toHaveCount(10);
  }
  await page.getByRole("tab", { name: "Definitions", exact: true }).click();
  const definitions = page.getByRole("region", { name: "Definitions", exact: true });
  await definitions.getByRole("columnheader", { name: "Definition", exact: true }).first().click();
  await expect(definitions.locator("tbody tr[data-row-key]").first()).toContainText(`${prefix}-00`);
  await definitions.getByRole("columnheader", { name: "Definition", exact: true }).first().click();
  await expect(definitions.locator("tbody tr[data-row-key]").first()).toContainText(`${prefix}-11`);
  await definitions.getByRole("button", { name: "filter", exact: true }).click();
  const filter = page.locator(".ant-table-filter-dropdown");
  await filter.getByRole("menuitem").filter({ hasText: "Published" }).click();
  await filter.getByRole("button", { name: "OK", exact: true }).click();
  await expect(definitions.locator("tbody tr[data-row-key]")).toHaveCount(0);
  await definitions.getByRole("button", { name: "filter", exact: true }).click();
  await filter.getByRole("button", { name: "Reset", exact: true }).click();
  await filter.getByRole("button", { name: "OK", exact: true }).click();
  await expect(definitions.locator("tbody tr[data-row-key]")).toHaveCount(10);
  await page.getByRole("tab", { name: "Event sources", exact: true }).click();
  const sources = page.getByRole("region", { name: "Event sources", exact: true });
  await sources.getByLabel("Search event sources").fill("");
  await sources.getByRole("button", { name: "filter", exact: true }).click();
  await filter.getByRole("menuitem").filter({ hasText: "Revoked" }).click();
  await filter.getByRole("button", { name: "OK", exact: true }).click();
  await expect(sources.getByText("No matches. Clear search or filters to see all results.")).toBeVisible();
  await sources.getByRole("button", { name: "filter", exact: true }).click();
  await filter.getByRole("button", { name: "Reset", exact: true }).click();
  await filter.getByRole("button", { name: "OK", exact: true }).click();
  await expect(sources.locator("tbody tr[data-row-key]")).toHaveCount(10);
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(390);
});

test("Instance navigation ignores a delayed response for the previously opened instance", async ({ page }) => {
  await page.goto("/");
  await page.waitForFunction(() => localStorage.getItem("agent-core.owner-capability"));
  const token = await page.evaluate(() => localStorage.getItem("agent-core.owner-capability"));
  const prefix = `navigation-${Date.now()}`;
  const ids: string[] = [];
  for (const name of [`${prefix}-Alpha`, `${prefix}-Beta`]) {
    const response = await page.request.post("/api/v2/admin/agent-instances", {
      headers: { "X-AgentCore-Owner-Capability": token! },
      data: { definitionId: "examiner", version: 1, persona: { name, role: "Inspector", description: "Navigation fixture", tone: "Clear" } }
    });
    expect(response.ok()).toBeTruthy();
    ids.push((await response.json()).instanceId);
  }
  let release!: () => void;
  let fetched!: () => void;
  const hold = new Promise<void>(resolve => { release = resolve; });
  const intercepted = new Promise<void>(resolve => { fetched = resolve; });
  const pattern = `**/admin/instances/${ids[0]}/effective-config`;
  await page.route(pattern, async route => {
    const response = await route.fetch();
    fetched();
    await hold;
    await route.fulfill({ response });
  });
  try {
    await page.goto(`/admin/instances/${ids[0]}`);
    await intercepted;
    await page.getByRole("button", { name: /Back to inventory/ }).click();
    await page.getByLabel("Search instances").fill(`${prefix}-Beta`);
    await page.getByRole("button", { name: `${prefix}-Beta · examiner`, exact: true }).click();
    await expect(page.getByLabel("Persona name", { exact: true })).toHaveValue(`${prefix}-Beta`);
    const oldResponse = page.waitForResponse(response => response.url().endsWith(`/instances/${ids[0]}/effective-config`));
    release();
    await oldResponse;
    await page.unroute(pattern);
    await expect(page.getByRole("heading", { name: `${prefix}-Beta`, exact: true })).toBeVisible();
    await expect(page.getByLabel("Persona name", { exact: true })).toHaveValue(`${prefix}-Beta`);
  } finally {
    release();
    await page.unrouteAll({ behavior: "wait" });
  }
});

test("Definition history navigation closes the previous definition's draft editor", async ({ page }) => {
  await page.goto("/");
  await page.waitForFunction(() => localStorage.getItem("agent-core.owner-capability"));
  const token = await page.evaluate(() => localStorage.getItem("agent-core.owner-capability"));
  const definitionId = `history-draft-${Date.now()}`;
  const response = await page.request.post("/api/v2/admin/definition-drafts/new", {
    headers: { "X-AgentCore-Owner-Capability": token! }, data: { definitionId }
  });
  expect(response.ok()).toBeTruthy();
  await page.goto("/admin/definitions/examiner");
  await expect(page.getByRole("heading", { name: "Examiner", exact: true })).toBeVisible();
  await page.evaluate(id => {
    history.pushState({}, "", `/admin/definitions/${id}`);
    dispatchEvent(new PopStateEvent("popstate"));
  }, definitionId);
  await expect(draftEditorSection(page)).toBeVisible();
  await page.goBack();
  await expect(page.getByRole("heading", { name: "Examiner", exact: true })).toBeVisible();
  await expect(draftEditorSection(page)).toHaveCount(0);
  await page.goForward();
  await expect(draftEditorSection(page)).toBeVisible();
});

test("Lifecycle and version changes preserve unsaved persona edits", async ({ page }) => {
  await page.goto("/");
  await page.waitForFunction(() => localStorage.getItem("agent-core.owner-capability"));
  const token = await page.evaluate(() => localStorage.getItem("agent-core.owner-capability"));
  const headers = { "X-AgentCore-Owner-Capability": token! };
  const definitionId = `persona-retention-${Date.now()}`;
  const candidate = await (await page.request.get("/api/v2/admin/definitions/examiner/versions/1?sourceKind=ForkBuiltIn", { headers })).json();
  for (const version of [1, 2]) {
    const created = version === 1
      ? await page.request.post("/api/v2/admin/definition-drafts", { headers,
        data: { definitionId, candidate: { ...candidate, definitionId, systemInstructions: "Retention fixture" } } })
      : await page.request.post("/api/v2/admin/definition-drafts/fork", { headers,
        data: { definitionId, sourceVersion: 1, sourceKind: "ForkDurable" } });
    expect(created.ok()).toBeTruthy();
    const draft = await created.json();
    const published = await page.request.post(`/api/v2/admin/definition-drafts/${draft.draftId}/publish`, {
      headers, data: { expectedRevision: draft.revision } });
    expect(published.ok()).toBeTruthy();
  }
  const created = await page.request.post("/api/v2/admin/agent-instances", { headers,
    data: { definitionId, version: 1 } });
  expect(created.ok()).toBeTruthy();
  const { instanceId } = await created.json();
  await page.goto(`/admin/instances/${instanceId}`);
  await page.getByLabel("Persona name", { exact: true }).fill("Retained unsaved persona");
  await page.getByLabel("Target definition version", { exact: true }).click();
  await page.locator(".ant-select-item-option").filter({ hasText: "v2" }).click();
  await page.getByRole("button", { name: "Upgrade to v2", exact: true }).click();
  await expect(page.getByText("Active version set to v2.", { exact: true })).toBeVisible();
  await expect(page.getByLabel("Persona name", { exact: true })).toHaveValue("Retained unsaved persona");
  await page.getByRole("tab", { name: "JSON", exact: true }).click();
  await page.getByLabel("Persona JSON", { exact: true }).fill("{invalid json");
  await page.getByRole("button", { name: "Archive instance", exact: true }).click();
  await page.getByRole("dialog").getByRole("button", { name: "Archive", exact: true }).click();
  await expect(page.getByRole("button", { name: "Unarchive instance", exact: true })).toBeVisible();
  await expect(page.getByLabel("Persona JSON", { exact: true })).toHaveValue("{invalid json");
  await page.getByRole("button", { name: "Unarchive instance", exact: true }).click();
  await expect(page.getByRole("button", { name: "Archive instance", exact: true })).toBeVisible();
  await expect(page.getByLabel("Persona JSON", { exact: true })).toHaveValue("{invalid json");
});

test("Behavior updates preserve persona edits through a failed refresh and retry", async ({ page }) => {
  await page.goto("/");
  await page.waitForFunction(() => localStorage.getItem("agent-core.owner-capability"));
  const token = await page.evaluate(() => localStorage.getItem("agent-core.owner-capability"));
  const name = `refresh-${Date.now()}`;
  const response = await page.request.post("/api/v2/admin/agent-instances", {
    headers: { "X-AgentCore-Owner-Capability": token! },
    data: { definitionId: "examiner", version: 1, persona: { name, role: "Inspector", description: "Refresh fixture", tone: "Clear" } }
  });
  expect(response.ok()).toBeTruthy();
  const { instanceId } = await response.json();
  await page.goto(`/admin/instances/${instanceId}`);
  await page.getByLabel("Persona name", { exact: true }).fill("Unsaved persona survives");
  await page.getByRole("tab", { name: "Automation", exact: true }).click();
  await page.getByRole("tab", { name: "Policies & models", exact: true }).click();
  await page.getByRole("combobox", { name: "Authoring mode", exact: true }).click();
  await page.locator(".ant-select-item-option").filter({ hasText: "Assisted" }).click();
  await page.getByLabel("Knowledge & resources", { exact: true }).check();
  const pattern = `**/admin/instances/${instanceId}/effective-config`;
  await page.route(pattern, route => route.fulfill({ status: 503, contentType: "application/problem+json",
    body: JSON.stringify({ title: "Configuration refresh unavailable", status: 503 }) }));
  await page.getByRole("button", { name: "Save authoring policy", exact: true }).click();
  await expect(page.getByRole("alert").filter({ hasText: "Configuration refresh unavailable" })).toBeVisible();
  await expect(page.getByLabel("Persona name", { exact: true })).toHaveValue("Unsaved persona survives");
  await expect(page.locator("[inert] .admin-instance-tabs")).toHaveCount(1);
  await page.unroute(pattern);
  await page.getByRole("button", { name: "Retry", exact: true }).click();
  await page.getByRole("tab", { name: "Identity & version", exact: true }).click();
  await expect(page.getByLabel("Persona name", { exact: true })).toHaveValue("Unsaved persona survives");
  await page.getByRole("button", { name: "Save persona", exact: true }).click();
  await expect(page.getByText("Persona updated.", { exact: true })).toBeVisible();
  await page.reload();
  await expect(page.getByLabel("Persona name", { exact: true })).toHaveValue("Unsaved persona survives");
});

test("Version inspection creates no draft and the shared actions publish from Test & Publish", async ({ page }) => {
  test.setTimeout(120_000);
  const errors: string[] = [];
  const failedRequests: string[] = [];
  page.on("pageerror", error => errors.push(error.message));
  page.on("requestfailed", request => failedRequests.push(request.url()));
  await page.goto("/");
  await page.waitForFunction(() => localStorage.getItem("agent-core.owner-capability"));
  const token = await page.evaluate(() => localStorage.getItem("agent-core.owner-capability"));
  const headers = { "X-AgentCore-Owner-Capability": token! };
  const draftsBefore = await (await page.request.get("/api/v2/admin/definition-drafts", { headers })).json();
  await page.goto("/admin/definitions/examiner");
  await page.getByRole("textbox", { name: "Search versions", exact: true }).fill("Built-in");
  await page.getByRole("button", { name: "View v1 (builtIn)", exact: true }).click();
  const details = page.getByRole("region", { name: "Version details", exact: true });
  await expect(details.getByLabel("System instructions", { exact: true })).toHaveAttribute("readonly", "");
  const instructions = details.getByLabel("System instructions", { exact: true });
  await instructions.focus();
  await expect(instructions).toBeFocused();
  await instructions.press("ControlOrMeta+A");
  expect(await instructions.evaluate((element: HTMLTextAreaElement) => element.selectionEnd - element.selectionStart)).toBeGreaterThan(0);
  const retainedInstructions = await instructions.inputValue();
  await instructions.press("x");
  await expect(instructions).toHaveValue(retainedInstructions);

  await expect(details.getByRole("button", { name: "Add goal", exact: true })).toHaveCount(0);
  await details.getByText("Advanced JSON", { exact: true }).click();
  await expect(details.getByRole("textbox", { name: "Advanced JSON", exact: true })).toHaveAttribute("readonly");
  await expect(details.getByRole("button", { name: "Save draft", exact: true })).toHaveCount(0);
  expect(await (await page.request.get("/api/v2/admin/definition-drafts", { headers })).json()).toEqual(draftsBefore);
  await page.getByRole("dialog", { name: "Version details", exact: true }).getByRole("button", { name: "Close", exact: true }).click();
  await page.getByRole("button", { name: /Back to inventory/ }).click();
  const definitionId = `polish-version-${Date.now()}`;
  await page.getByRole("button", { name: "New definition", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "New definition", exact: true });
  await dialog.getByLabel("Definition ID").fill(definitionId);
  await dialog.getByRole("button", { name: "Create draft", exact: true }).click();
  const editor = draftEditorSection(page);
  await editor.getByLabel("System instructions", { exact: true }).fill("Follow the published inspection guide.");
  const longGoal = "Inspect the complete published configuration, including long identity values and goals, without truncation on a narrow screen. Keep the version immutable and preserve the current collection context.";
  await editor.getByLabel("Goal 1", { exact: true }).fill(longGoal);
  await editor.getByRole("tab", { name: "Resources", exact: true }).click();
  await expect(editor.getByRole("button", { name: "Save draft", exact: true })).toBeEnabled();
  await editor.getByRole("button", { name: "Save draft", exact: true }).click();
  await expect(page.getByText("Draft saved.", { exact: true })).toBeVisible();
  await editor.getByRole("tab", { name: "Test & Publish", exact: true }).click();
  await expect(editor.getByRole("button", { name: "Publish…", exact: true })).toBeDisabled();
  await completeDefinitionDraftPublishGate(page, editor);
  await editor.getByRole("button", { name: "Publish…", exact: true }).click();
  await page.getByRole("dialog").getByRole("button", { name: "Publish", exact: true }).click();
  await expect(page.getByText("Published version 1.", { exact: true })).toBeVisible();
  await expect(page.getByRole("tab", { name: "Versions", exact: true })).toHaveAttribute("aria-selected", "true");
  await page.getByRole("tab", { name: "Drafts", exact: true }).click();
  await expect(page.getByText("No drafts yet. Fork a catalog version to start.", { exact: true })).toBeVisible();
  await page.getByRole("tab", { name: "Versions", exact: true }).click();
  const publishedRow = page.getByRole("button", { name: "View v1 (durable)", exact: true }).locator("xpath=ancestor::tr");
  await expect(publishedRow.getByRole("cell")).toHaveCount(7);
  await expect(publishedRow.getByRole("cell").nth(4)).toHaveText("1");
  await expect(publishedRow.getByRole("cell").nth(5)).toHaveText("0");
  expect((await publishedRow.boundingBox())!.height).toBeLessThan(60);
  await page.getByRole("button", { name: "View v1 (durable)", exact: true }).click();
  await expect(details.getByLabel("System instructions", { exact: true })).toHaveValue("Follow the published inspection guide.");
  await page.setViewportSize({ width: 390, height: 844 });
  const goal = details.getByLabel("Goal 1", { exact: true });
  await expect(goal).toHaveValue(longGoal);
  await expect.poll(() => goal.evaluate(element => element.clientHeight)).toBeGreaterThan(32);
  await expect.poll(() => goal.evaluate(element => element.scrollHeight - element.clientHeight)).toBeLessThanOrEqual(1);
  await page.getByRole("dialog", { name: "Version details", exact: true }).getByRole("button", { name: "Close", exact: true }).click();
  await page.setViewportSize({ width: 1280, height: 720 });
  await page.getByRole("button", { name: /Back to inventory/ }).click();
  await page.getByRole('tab', { name: 'Instances', exact: true }).click();
  await page.getByRole("button", { name: "New instance", exact: true }).click();
  const instanceDialog = page.getByRole("dialog", { name: "New instance", exact: true });
  await instanceDialog.getByLabel("Definition", { exact: true }).click();
  await instanceDialog.getByRole("combobox", { name: "Definition", exact: true }).fill(definitionId);
  await page.locator(".ant-select-item-option").filter({ hasText: definitionId }).last().click();
  await instanceDialog.getByRole("button", { name: "Create instance", exact: true }).click();
  const persona = page.getByLabel("Persona name", { exact: true });
  await persona.fill("Unsaved inspector");
  for (const name of ["Continuity", "Automation", "Runs", "Credentials", "Effective configuration"]) {
    await page.getByRole("tab", { name, exact: true }).click();
    await expect(page.getByRole("tabpanel", { name, exact: true })).toBeVisible();
  }
  await page.getByRole("tab", { name: "Identity & version", exact: true }).click();
  await expect(persona).toHaveValue("Unsaved inspector");
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(390);
  for (const name of ["Continuity", "Automation", "Runs", "Credentials", "Effective configuration"]) {
    await expect(page.getByRole("tab", { name, exact: true })).toBeInViewport();
  }
  expect(errors).toEqual([]);
  expect(failedRequests).toEqual([]);
});

test("Collection navigation and version drawer preserve context and keyboard focus", async ({ page }) => {
  await page.goto("/");
  await page.waitForFunction(() => localStorage.getItem("agent-core.owner-capability"));
  await page.goto("/admin");
  const definitions = page.getByRole("region", { name: "Definitions", exact: true });
  await definitions.getByLabel("Search definitions").fill("examiner");
  const row = definitions.getByRole("button", { name: "Examiner · examiner", exact: true });
  const definitionRow = row.locator("xpath=ancestor::tr");
  await expect(definitions.getByRole("columnheader", { name: "Definition ID", exact: true })).toBeVisible();
  await expect(definitionRow.getByRole("cell", { name: "examiner", exact: true })).toBeVisible();
  const cells = await definitionRow.getByRole("cell").all();
  expect((await cells[1].boundingBox())!.x).toBeGreaterThan((await row.boundingBox())!.x);
  const content = await page.locator(".admin-content").boundingBox();
  expect(content!.width).toBe(page.viewportSize()!.width);
  await page.getByRole("tab", { name: "Instances", exact: true }).click();
  await expect(page).toHaveURL(/\/admin\/instances$/);
  await page.goBack();
  await expect(page.getByRole("tab", { name: "Definitions", exact: true })).toHaveAttribute("aria-selected", "true");
  await expect(definitions.getByLabel("Search definitions")).toHaveValue("examiner");
  await row.click();
  const versions = page.getByRole("region", { name: "Definition versions", exact: true });
  await versions.getByLabel("Search versions").fill("Built-in");
  const trigger = versions.getByRole("button", { name: "View v1 (builtIn)", exact: true });
  await trigger.focus();
  const scroll = await page.evaluate(() => window.scrollY);
  await trigger.press("Enter");
  const drawer = page.getByRole("dialog", { name: "Version details", exact: true });
  await expect(drawer.getByLabel("System instructions", { exact: true })).not.toBeDisabled();
  await expect(drawer.getByLabel("System instructions", { exact: true })).toHaveAttribute("readonly", "");
  expect(await page.evaluate(() => window.scrollY)).toBe(scroll);
  await page.keyboard.press("Escape");
  await expect(drawer).toBeHidden();
  await expect(trigger).toBeFocused();
  await expect(versions.getByLabel("Search versions")).toHaveValue("Built-in");
  expect(await page.evaluate(() => window.scrollY)).toBe(scroll);
  await page.setViewportSize({ width: 390, height: 844 });
  await trigger.click();
  await expect(drawer).toBeVisible();
  await expect.poll(async () => (await drawer.boundingBox())?.x).toBe(0);
  expect((await drawer.boundingBox())!.width).toBe(390);
  await drawer.getByRole("button", { name: "Close", exact: true }).click();
  await expect(trigger).toBeFocused();
  await page.goto("/admin/event-sources");
  await expect(page.getByRole("tab", { name: "Event sources", exact: true })).toHaveAttribute("aria-selected", "true");
  await page.reload();
  await expect(page.getByRole("region", { name: "Event sources", exact: true })).toBeVisible();
  await page.goto("/admin/instances");
  await page.locator(".admin-collection-name").first().click();
  await page.getByRole("button", { name: /Back to inventory/ }).click();
  await expect(page).toHaveURL(/\/admin\/instances$/);
  await expect(page.getByRole("tab", { name: "Instances", exact: true })).toHaveAttribute("aria-selected", "true");
});
