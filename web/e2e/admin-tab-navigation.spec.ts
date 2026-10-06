import { expect, test } from "@playwright/test";

for (const width of [1440, 390]) {
  test(`Admin tab paths survive refresh and history at ${width}px`, async ({ page }) => {
    const consoleErrors: string[] = [];
    const failedRequests: string[] = [];
    page.on("console", message => { if (message.type() === "error") consoleErrors.push(message.text()); });
    page.on("requestfailed", request => { failedRequests.push(`${request.method()} ${request.url()}`); });
    await page.setViewportSize({ width, height: 900 });
    await page.goto("/");
    await page.waitForFunction(() => localStorage.getItem("agent-core.owner-capability"));
    const token = await page.evaluate(() => localStorage.getItem("agent-core.owner-capability"));
    const headers = { "X-AgentCore-Owner-Capability": token! };
    const created = await page.request.post("/api/v2/admin/agent-instances", {
      headers, data: { definitionId: "examiner", version: 1,
        persona: { name: "Tab navigation fixture", role: "Inspector", description: "Synthetic navigation check", tone: "Clear" } }
    });
    expect(created.ok()).toBeTruthy();
    const { instanceId } = await created.json();
    const base = `/admin/instances/${instanceId}`;

    await page.goto("/admin/definitions/examiner");
    await page.getByRole("tab", { name: "Drafts", exact: true }).click();
    await expect(page).toHaveURL(/\/definitions\/examiner\/drafts$/);
    await page.reload();
    await expect(page.getByRole("tab", { name: "Drafts", exact: true })).toHaveAttribute("aria-selected", "true");
    await page.getByRole("tab", { name: "Versions", exact: true }).click();
    await expect(page).toHaveURL(/\/definitions\/examiner\/versions$/);
    await page.goBack();
    await expect(page.getByRole("tab", { name: "Drafts", exact: true })).toHaveAttribute("aria-selected", "true");
    await page.goForward();
    await expect(page.getByRole("tab", { name: "Versions", exact: true })).toHaveAttribute("aria-selected", "true");

    await page.goto(base);
    await page.getByLabel("Persona name", { exact: true }).fill("Unsaved tab navigation edit");
    let configurationReads = 0;
    page.on("request", request => { if (request.url().endsWith(`${instanceId}/effective-config`)) configurationReads++; });
    await page.getByRole("tab", { name: "Automation", exact: true }).click();
    await page.getByRole("tab", { name: "Thoughts", exact: true }).click();
    await expect(page).toHaveURL(`${new URL(base, page.url())}/automation/thoughts`);
    await page.getByRole("tab", { name: "Identity & version", exact: true }).click();
    await expect(page.getByLabel("Persona name", { exact: true })).toHaveValue("Unsaved tab navigation edit");
    expect(configurationReads).toBe(0);

    for (const [tab, section, path] of [
      ["Identity & version", undefined, "identity"],
      ["Continuity", "Memory", "continuity/memory"],
      ["Continuity", "Experience", "continuity/experience"],
      ["Automation", "Schedules", "automation/schedules"],
      ["Automation", "Thoughts", "automation/thoughts"],
      ["Automation", "Policies & models", "automation/controls"],
      ["Runs", undefined, "runs"],
      ["Connections", undefined, "connections"],
      ["Effective configuration", undefined, "effective"]
    ] as const) {
      await page.getByRole("tab", { name: tab, exact: true }).click();
      if (section) await page.getByRole("tab", { name: section, exact: true }).click();
      await expect(page).toHaveURL(new URL(`${base}/${path}`, page.url()).href);
      await page.reload();
      await expect(page.getByRole("tab", { name: tab, exact: true })).toHaveAttribute("aria-selected", "true");
      if (section) await expect(page.getByRole("tab", { name: section, exact: true })).toHaveAttribute("aria-selected", "true");
      expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(width);
    }
    await page.goto(`${base}/automation/unknown-section`);
    await expect(page.getByRole("tab", { name: "Schedules", exact: true })).toHaveAttribute("aria-selected", "true");
    await page.getByRole("tab", { name: "Thoughts", exact: true }).click();
    await page.getByRole("tab", { name: "Continuity", exact: true }).click();
    await page.getByRole("tab", { name: "Experience", exact: true }).click();
    await page.goBack();
    await expect(page.getByRole("tab", { name: "Memory", exact: true })).toHaveAttribute("aria-selected", "true");
    await page.goForward();
    await expect(page.getByRole("tab", { name: "Experience", exact: true })).toHaveAttribute("aria-selected", "true");

    // Archived instances cannot expose Automation; the URL follows the existing Identity fallback.
    await page.getByRole("tab", { name: "Identity & version", exact: true }).click();
    await page.getByRole("button", { name: "Archive instance", exact: true }).click();
    await page.getByRole("dialog").getByRole("button", { name: "Archive", exact: true }).click();
    await expect(page.getByRole("button", { name: "Unarchive instance", exact: true })).toBeVisible();
    await page.goto(`${base}/automation/thoughts`);
    await expect(page).toHaveURL(new URL(`${base}/identity`, page.url()).href);
    await expect(page.getByRole("tab", { name: "Identity & version", exact: true })).toHaveAttribute("aria-selected", "true");
    await page.getByRole("button", { name: /Back to inventory/ }).click();
    await expect(page).toHaveURL(/\/admin\/instances$/);
    expect(failedRequests).toEqual([]);
    expect(consoleErrors).toEqual([]);
    const config = await (await page.request.get(`/api/v2/admin/instances/${instanceId}/effective-config`, { headers })).json();
    const deleted = await page.request.delete(`/api/v2/admin/agent-instances/${instanceId}`, {
      headers, data: { expectedRevision: config.instanceRevision }
    });
    expect(deleted.ok()).toBeTruthy();
  });
}

for (const outcome of ["success", "failure"] as const) {
  test(`A delayed draft ${outcome} cannot take over a newer tab navigation`, async ({ page }) => {
    await page.goto("/");
    await page.waitForFunction(() => localStorage.getItem("agent-core.owner-capability"));
    const token = await page.evaluate(() => localStorage.getItem("agent-core.owner-capability"));
    const headers = { "X-AgentCore-Owner-Capability": token! };
    const created = await page.request.post("/api/v2/admin/definition-drafts/fork", {
      headers, data: { definitionId: "examiner", sourceVersion: 1, sourceKind: "ForkBuiltIn" }
    });
    expect(created.ok()).toBeTruthy();
    const draft = await created.json();
    let release!: () => void;
    let fetched!: () => void;
    const hold = new Promise<void>(resolve => { release = resolve; });
    const intercepted = new Promise<void>(resolve => { fetched = resolve; });
    const pattern = `**/api/v2/admin/definition-drafts/${draft.draftId}`;
    await page.route(pattern, async route => {
      const response = await route.fetch();
      fetched();
      await hold;
      if (outcome === "success") await route.fulfill({ response });
      else await route.fulfill({ status: 503, contentType: "application/problem+json",
        body: JSON.stringify({ title: "Late draft read failure" }) });
    });
    try {
      await page.goto("/admin/definitions/examiner/versions");
      await page.getByRole("tab", { name: "Drafts", exact: true }).click();
      await page.locator(`tr[data-row-key="${draft.draftId}"]`).getByRole("button", { name: `Draft rev ${draft.revision} · ForkBuiltIn v1`, exact: true }).click();
      await intercepted;
      await page.goBack();
      await expect(page.getByRole("tab", { name: "Versions", exact: true })).toHaveAttribute("aria-selected", "true");
      await expect(page.locator(".admin-draft-version-select").getByRole("combobox")).toBeEnabled();
      const delivered = page.waitForResponse(response => response.url().endsWith(`/definition-drafts/${draft.draftId}`));
      release();
      await (await delivered).finished();
      await expect(page.locator(".admin-draft-version-select").getByRole("combobox")).toBeEnabled();
      await expect(page).toHaveURL(/\/examiner\/versions$/);
      await expect(page.getByRole("tab", { name: "Versions", exact: true })).toHaveAttribute("aria-selected", "true");
      await expect(page.getByRole("region", { name: "Draft editor", exact: true })).toHaveCount(0);
      await expect(page.getByText("Late draft read failure", { exact: true })).toHaveCount(0);
      await page.unroute(pattern);
      await page.getByRole("tab", { name: "Drafts", exact: true }).click();
      await page.locator(`tr[data-row-key="${draft.draftId}"]`).getByRole("button", { name: `Draft rev ${draft.revision} · ForkBuiltIn v1`, exact: true }).click();
      await expect(page.getByRole("region", { name: "Draft editor", exact: true })).toBeVisible();
      await expect(page).toHaveURL(/\/examiner\/drafts$/);
      await page.reload();
      await expect(page.getByRole("table", { name: "Definition drafts table", exact: true })).toBeVisible();
      await expect(page.getByRole("region", { name: "Draft editor", exact: true })).toHaveCount(0);
    } finally {
      release();
      await page.unrouteAll({ behavior: "wait" });
      const deleted = await page.request.delete(`/api/v2/admin/definition-drafts/${draft.draftId}?expectedRevision=${draft.revision}`, { headers });
      expect(deleted.ok()).toBeTruthy();
    }
  });
}
