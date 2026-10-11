import { test, expect, type Page } from "@playwright/test";
import {
  selectInstanceIdentity,
  INSTANCE_DEFINITIONS,
} from "./support/instance-identity";
import { waitForResponseSettled } from "./support/response-settled";
async function headers(page: Page) {
  return {
    "X-AgentCore-Owner-Capability": (await page.evaluate(() =>
      localStorage.getItem("agent-core.owner-capability"),
    ))!,
  };
}
async function setup(page: Page) {
  await page.goto("/");
  await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.examiner);
  await page.getByLabel("Message", { exact: true }).fill("Hello");
  await page.getByRole("button", { name: "Send", exact: true }).click();
  await expect(page.locator(".chat-message-assistant")).toContainText(
    "Hello from synthetic.",
  );
  await waitForResponseSettled(page);
  const sessionId = page.url().split("/c/")[1];
  const h = await headers(page);
  const session = await page.request.get(`/api/v1/sessions/${sessionId}`, {
    headers: h,
  });
  const instanceId = (await session.json()).agentInstanceId;
  for (const id of ["review", "check"]) {
    const r = await page.request.post(
      `/api/v2/admin/agent-instances/${instanceId}/skills`,
      {
        headers: h,
        data: {
          id,
          name: "Review",
          description: `${id} the task`,
          procedure: `${id.toUpperCase()}_PROCEDURE`,
          projection: "OnDemand",
          enabled: true,
          requiredCapabilities: [],
        },
      },
    );
    expect(r.ok(), await r.text()).toBe(true);
  }
  return { sessionId, instanceId, h };
}
async function skill(page: Page, id: string) {
  const editor = page.getByLabel("Message", { exact: true });
  await editor.pressSequentially("/" + id);
  const choices = page.getByRole("listbox", { name: "Composer choices" });
  await expect(choices).toBeVisible();
  await expect(
    page.getByRole("option").filter({ hasText: `instance:${id}` }),
  ).toBeVisible();
  const labels = await page.getByRole("option").allTextContents();
  const index = labels.findIndex((text) => text.includes(`instance:${id}`));
  for (let i = 0; i < index; i++) await editor.press("ArrowDown");
  await editor.press("Enter");
  await expect(editor.locator(`[data-skill-key="instance:${id}"]`)).toHaveCount(
    1,
  );
}

test("removing a typed trigger closes suggestions, including an empty paragraph", async ({ page }) => {
  await setup(page);
  const editor = page.getByLabel("Message", { exact: true });
  const choices = page.getByRole("listbox", { name: "Composer choices" });
  for (const trigger of ["/", "@", "/review"]) {
    await editor.pressSequentially(trigger);
    await expect(choices).toBeVisible();
    for (let i = 0; i < trigger.length; i++) await editor.press("Backspace");
    await expect(editor).toHaveText("");
    await expect(choices).toBeHidden();
  }
  await page.getByRole("button", { name: "Add content" }).click();
  await page.getByRole("menuitem", { name: "Use Skill" }).click();
  await expect(choices).toBeVisible();
  const search = page.getByRole("combobox", { name: "Search composer choices" });
  await search.fill("review");
  await search.fill("");
  await expect(choices).toBeVisible();
});

test("a Skill disabled after selection rejects safely and preserves its draft for recovery", async ({ page }) => {
  const { instanceId, h } = await setup(page);
  await skill(page, "review");
  const path = `/api/v2/admin/agent-instances/${instanceId}/skills/instance:review/enabled`;
  const disabled = await page.request.put(path, { headers: h, data: { expectedRevision: 1, enabled: false } });
  expect(disabled.ok(), await disabled.text()).toBe(true);
  const revision = (await disabled.json()).revision;
  await page.getByRole("button", { name: "Send", exact: true }).click();
  const editor = page.getByLabel("Message", { exact: true });
  await expect(editor.locator('[data-skill-key="instance:review"]')).toHaveCount(1);
  await expect(page.getByText(/Selected Skill instance:review is unavailable/)).toBeVisible();
  await expect(page.locator(".chat-message-user")).toHaveCount(1);
  await expect(page.locator(".chat-message-assistant")).toHaveCount(1);
  const enabled = await page.request.put(path, { headers: h, data: { expectedRevision: revision, enabled: true } });
  expect(enabled.ok(), await enabled.text()).toBe(true);
  await page.getByRole("button", { name: "Send", exact: true }).click();
  await expect(page.locator(".chat-message-assistant")).toHaveCount(2);
  await waitForResponseSettled(page);
  await expect(page.locator(".chat-message-user").last().locator(".composer-chip")).toHaveText("/Review");
});

for (const count of [1, 2]) {
  test(`${count} Skills alone send as a first turn, retain title and history, and leave later turns unactivated`, async ({ page }) => {
    const { instanceId, h } = await setup(page);
    await page.goto("/");
    const identity = page.getByRole("combobox", { name: "Identity" });
    await identity.click();
    await identity.fill(instanceId.replace(/-/g, "").slice(-8));
    await page.locator(".ant-select-item-option").filter({ hasText: instanceId.replace(/-/g, "").slice(-8) }).click();
    await skill(page, "review");
    if (count === 2) await skill(page, "check");
    await expect(page.getByRole("button", { name: "Send", exact: true })).toBeEnabled();
    await page.getByRole("button", { name: "Send", exact: true }).click();
    await expect(page.locator(".chat-message-assistant")).toContainText("Hello from synthetic.");
    await waitForResponseSettled(page);
    const sessionId = page.url().split("/c/")[1];
    const catalog = await (await page.request.get("/api/v2/sessions", { headers: h })).json();
    expect(catalog.items.find((s: any) => s.sessionId === sessionId).title).toBe(count === 1 ? "/review" : "/review + /check");
    await page.reload();
    await expect(page.locator(".chat-message-user .composer-chip")).toHaveCount(count);
    await expect(page.locator(".chat-message-user .composer-chip").first()).toHaveText("/Review");
    const renamed = await page.request.post(`/api/v2/sessions/${sessionId}/rename`, { headers: h, data: { title: "Chosen title" } });
    expect(renamed.ok()).toBe(true);
    await page.getByLabel("Message", { exact: true }).fill("Continue normally");
    await page.getByRole("button", { name: "Send", exact: true }).click();
    await expect(page.locator(".chat-message-assistant")).toHaveCount(2);
    await waitForResponseSettled(page);
    const runs = await (await page.request.get(`/api/v2/sessions/${sessionId}/agent-runs`, { headers: h })).json();
    expect(runs.items[0].composerInput?.explicitSkillKeys ?? []).toEqual([]);
    expect(runs.items[1].composerInput.explicitSkillKeys).toEqual(count === 1 ? ["instance:review"] : ["instance:review", "instance:check"]);
    const updated = await (await page.request.get("/api/v2/sessions", { headers: h })).json();
    expect(updated.items.find((s: any) => s.sessionId === sessionId).title).toBe("Chosen title");
  });
}

test("two Skills and two references survive send, history reload, and pinned inspection", async ({
  page,
}) => {
  const { sessionId, h } = await setup(page);
  const editor = page.getByLabel("Message", { exact: true });
  await skill(page, "review");
  await expect(
    page.getByRole("button", { name: "Send", exact: true }),
  ).toBeEnabled();
  await skill(page, "check");
  await editor.pressSequentially("Inspect ");
  await editor.pressSequentially("@");
  await expect(
    page.getByRole("option").filter({ hasText: "Hello" }).filter({ has: page.locator(".composer-choice-kind", { hasText: /^Chat$/ }) }),
  ).toBeVisible();
  await editor.press("Enter");
  await page.getByRole("button", { name: "Add content" }).click();
  await page.getByRole("menuitem", { name: "Reference resource" }).click();
  await page.getByRole("option").filter({ hasText: "instance:review" }).click();
  await expect(editor.locator("[data-composer-chip]")).toHaveCount(4);
  await page.locator("input.attach-input").setInputFiles({ name: "notes.txt", mimeType: "text/plain", buffer: Buffer.from("Supporting proposal notes") });
  await expect(page.getByRole("button", { name: "Remove notes.txt" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Send", exact: true })).toBeEnabled();
  await editor.press("Enter");
  await expect(
    page.locator(".chat-message-user").last().locator(".composer-chip"),
  ).toHaveCount(4);
  await expect(page.locator(".chat-message-user").last()).toContainText("notes.txt");
  await waitForResponseSettled(page);
  let input: any;
  await expect
    .poll(
      async () => {
        const runs = await page.request.get(
          `/api/v2/sessions/${sessionId}/agent-runs`,
          { headers: h },
        );
        input = (await runs.json()).items.find(
          (r: any) => r.composerInput?.explicitSkillKeys.length === 2,
        )?.composerInput;
        return input?.explicitSkillKeys;
      },
      { timeout: 30000 },
    )
    .toEqual(["instance:review", "instance:check"]);
  expect(input.explicitSkillKeys).toEqual([
    "instance:review",
    "instance:check",
  ]);
  expect(input.references).toHaveLength(2);
  expect(input.references.every((r: any) => r.status === "valid")).toBe(true);
  await page.reload();
  await expect(
    page.locator(".chat-message-user").last().locator(".composer-chip"),
  ).toHaveCount(4);
});

test("atomic editing, undo, literal paste and Vietnamese composition keep keyboard boundaries", async ({
  page,
}) => {
  await setup(page);
  const editor = page.getByLabel("Message", { exact: true });
  await skill(page, "review");
  await editor.press("ControlOrMeta+z");
  await expect(editor.locator("[data-composer-chip]")).toHaveCount(0);
  await editor.press("ControlOrMeta+Shift+z");
  await expect(editor.locator("[data-composer-chip]")).toHaveCount(1);
  await editor.press("ControlOrMeta+a");
  await editor.press("Backspace");
  await expect(editor).toHaveText("");
  await editor.evaluate((el) => {
    const event = new ClipboardEvent("paste", {
      bubbles: true,
      cancelable: true,
      clipboardData: new DataTransfer(),
    });
    event.clipboardData!.setData("text/plain", "/review @literal");
    el.dispatchEvent(event);
  });
  await expect(editor).toContainText("/review @literal");
  await expect(page.getByRole("dialog", { name: "Use Skill" })).toBeHidden();
  await editor.press("ControlOrMeta+a"); await editor.press("Backspace");
  await editor.pressSequentially("/"); await expect(page.getByRole("dialog",{name:"Use Skill"})).toBeVisible();
  await editor.press("Escape"); await editor.pressSequentially("review");
  await expect(page.getByRole("dialog",{name:"Use Skill"})).toBeHidden();
  const count = await page.locator(".chat-message-user").count();
  await editor.evaluate((el) => {
    el.dispatchEvent(
      new CompositionEvent("compositionstart", {
        bubbles: true,
        data: "tiếng",
      }),
    );
    el.dispatchEvent(
      new KeyboardEvent("keydown", {
        key: "Enter",
        isComposing: true,
        bubbles: true,
        cancelable: true,
      }),
    );
    el.dispatchEvent(
      new CompositionEvent("compositionend", { bubbles: true, data: "tiếng" }),
    );
  });
  expect(await page.locator(".chat-message-user").count()).toBe(count);
  await editor.press("Shift+Enter");
  await editor.pressSequentially("tiếng Việt");
  await editor.press("Enter");
  await expect(page.locator(".chat-message-user").last()).toContainText(
    "tiếng Việt",
  );
});
for (const width of [1440, 768, 390])
  test(`picker and chips remain reachable at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await setup(page);
    await skill(page, "review");
    await page.getByLabel("Message", { exact: true }).pressSequentially(" @");
    const picker = page.getByRole("dialog", { name: "Reference resource" });
    await expect(picker).toBeVisible();
    const box = await picker.boundingBox();
    expect(box!.x).toBeGreaterThanOrEqual(0);
    expect(box!.x + box!.width).toBeLessThanOrEqual(width);
    expect(box!.y).toBeGreaterThanOrEqual(0);
    await page.screenshot({
      path: `test-results/composer-${width}.png`,
      fullPage: true,
    });
    await page.getByLabel("Message", { exact: true }).press("Escape");
    await expect(picker).toBeHidden();
    await expect(page.getByLabel("Message", { exact: true })).toBeFocused();
  });

test("picker stays inside a short viewport with a tall draft and preserves insertion", async ({ page }) => {
  await setup(page);
  const editor = page.getByLabel("Message", { exact: true });
  const draft = Array(15).fill("Long draft line").join("\n");
  await editor.fill(draft);
  await editor.evaluate(el => {
    const range = document.createRange();
    range.setStart(el.querySelector("p")!.firstChild!, 0);
    range.collapse(true);
    const selection = window.getSelection()!;
    selection.removeAllRanges();
    selection.addRange(range);
  });
  await page.getByRole("button", { name: "Add content" }).click();
  await page.getByRole("menuitem", { name: "Use Skill" }).click();
  const picker = page.getByRole("dialog", { name: "Use Skill", exact: true });
  await expect(page.getByRole("option").first()).toBeVisible();
  for (const viewport of [{ width: 1440, height: 900 }, { width: 390, height: 320 }, { width: 320, height: 240 }]) {
    await page.setViewportSize(viewport);
    await expect.poll(async () => {
      const box = await picker.boundingBox();
      return Boolean(box && box.x >= 0 && box.y >= 0 && box.x + box.width <= viewport.width && box.y + box.height <= viewport.height);
    }).toBe(true);
  }
  const search = page.getByRole("combobox", { name: "Search composer choices" });
  await search.fill("instance:review");
  await expect(page.getByRole("option")).toHaveCount(1);
  await search.press("Enter");
  await expect(picker).toBeHidden();
  await expect(editor.locator('[data-skill-key="instance:review"]')).toHaveCount(1);
  await expect(editor).toContainText(draft);
});

test("scrolling many choices keeps search visible and long rows uncompressed", async ({ page }) => {
  const { instanceId, h } = await setup(page);
  await page.setViewportSize({ width: 390, height: 320 });
  const label = "Review international research and engineering proposals with Vietnamese teams";
  for (let n = 0; n < 6; n++) {
    const response = await page.request.post(`/api/v2/admin/agent-instances/${instanceId}/skills`, {
      headers: h,
      data: { id: `scroll-${n}`, name: label, description: `Proposal ${n}`, procedure: "Review the task.", projection: "OnDemand", enabled: true, requiredCapabilities: [] },
    });
    expect(response.ok(), await response.text()).toBe(true);
  }
  await page.getByRole("button", { name: "Add content" }).click();
  await page.getByRole("menuitem", { name: "Use Skill" }).click();
  const search = page.getByRole("combobox", { name: "Search composer choices" });
  await search.fill("instance:scroll-");
  await expect(page.getByRole("option")).toHaveCount(6);
  for (let n = 0; n < 5; n++) await search.press("ArrowDown");
  await expect.poll(async () => {
    const picker = await page.getByRole("dialog", { name: "Use Skill", exact: true }).boundingBox();
    const input = await search.boundingBox();
    const active = await page.locator('[role="option"][aria-selected="true"]').boundingBox();
    return Boolean(picker && input && active && input.y >= picker.y && input.y + input.height <= picker.y + picker.height && active.y >= input.y + input.height && active.y + active.height <= picker.y + picker.height);
  }).toBe(true);
  const rowsContainTheirText = await page.getByRole("option").evaluateAll(rows => rows.every(row => {
    const box = row.getBoundingClientRect();
    const body = row.querySelector(".composer-choice-body")!.getBoundingClientRect();
    return body.top >= box.top && body.bottom <= box.bottom;
  }));
  expect(rowsContainTheirText).toBe(true);
  await expect(search).toBeFocused();
  await search.press("Enter");
  await expect(page.getByLabel("Message", { exact: true }).locator('[data-skill-key="instance:scroll-5"]')).toHaveCount(1);
});

test("long mention labels wrap through draft and history without losing identity", async ({ page }) => {
  const { instanceId, h } = await setup(page);
  const label = "Review international research and engineering proposals with Vietnamese teams";
  const created = await page.request.post(`/api/v2/admin/agent-instances/${instanceId}/skills`, {
    headers: h,
    data: { id: "long-review", name: label, description: "Inspect the supporting proposal and summarize improvements.", procedure: "Review the task.", projection: "OnDemand", enabled: true, requiredCapabilities: [] },
  });
  expect(created.ok(), await created.text()).toBe(true);
  const editor = page.getByLabel("Message", { exact: true });
  await editor.pressSequentially("/long-review");
  const option = page.getByRole("option").filter({ hasText: "instance:long-review" });
  await expect(option).toBeVisible();
  for (const width of [1440, 768, 390]) {
    await page.setViewportSize({ width, height: 900 });
    await expect.poll(async () => {
      const bounds = await option.boundingBox();
      return Boolean(bounds && bounds.x >= 0 && bounds.x + bounds.width <= width);
    }).toBe(true);
    await expect(option.locator(".composer-choice-title")).toHaveText(label);
  }
  await editor.press("Enter");
  const chip = editor.locator(".composer-chip");
  await expect(chip).toHaveText(`/${label}`);
  await expect(chip).toHaveAttribute("title", /activates when sent/);
  expect(await editor.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
  await page.getByRole("button", { name: "Send", exact: true }).click();
  await waitForResponseSettled(page);
  await page.reload();
  const historicalChip = page.locator(".chat-message-user").last().locator(".composer-chip");
  await expect(historicalChip).toHaveText(`/${label}`);
  await expect(historicalChip).toHaveAttribute("title", "Skill · instance:long-review · invoked in this message");
  expect(await page.locator(".chat-message-user").last().evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
});

test("structured clipboard preserves multiline IDs and foreign-instance paste stays literal", async ({
  page,
}) => {
  await setup(page);
  const editor = page.getByLabel("Message", { exact: true });
  await skill(page, "review");
  await editor.pressSequentially("First line");
  await editor.press("Shift+Enter");
  await editor.pressSequentially("Second line");
  await editor.press("ControlOrMeta+a");
  const copied = await editor.evaluate((el) => {
    const data = new DataTransfer();
    el.dispatchEvent(
      new ClipboardEvent("copy", {
        bubbles: true,
        cancelable: true,
        clipboardData: data,
      }),
    );
    return {
      plain: data.getData("text/plain"),
      typed: data.getData("application/x-agent-core-parts"),
    };
  });
  expect(copied.plain).toContain("\n");
  expect(
    JSON.parse(copied.typed).parts.some((p: any) => p.kind === "invocation"),
  ).toBe(true);
  await editor.press("Backspace");
  await editor.evaluate((el, value) => {
    const data = new DataTransfer();
    data.setData("text/plain", value.plain);
    data.setData("application/x-agent-core-parts", value.typed);
    el.dispatchEvent(
      new ClipboardEvent("paste", {
        bubbles: true,
        cancelable: true,
        clipboardData: data,
      }),
    );
  }, copied);
  await expect(editor.locator("[data-composer-chip]")).toHaveCount(1);
  await expect(editor).toContainText("First line\nSecond line");
  await editor.press("ControlOrMeta+a");
  await editor.press("Backspace");
  await editor.evaluate((el, value) => {
    const data = new DataTransfer();
    data.setData("text/plain", value.plain);
    const typed = JSON.parse(value.typed);
    typed.instanceId = "00000000-0000-0000-0000-000000000001";
    data.setData("application/x-agent-core-parts", JSON.stringify(typed));
    el.dispatchEvent(
      new ClipboardEvent("paste", {
        bubbles: true,
        cancelable: true,
        clipboardData: data,
      }),
    );
  }, copied);
  await expect(editor.locator("[data-composer-chip]")).toHaveCount(0);
  await expect(editor).toContainText("/Review");
});


test("typed queued steering keeps chip identity and a newer draft", async ({page}) => {
 await setup(page);
 const editor=page.getByLabel("Message",{exact:true});
 await editor.fill("[test:durable-stream] Start work"); await editor.press("Enter");
 await expect(page.getByRole("button",{name:"Stop",exact:true})).toBeVisible();
 await skill(page,"review"); await editor.pressSequentially("Inspect queued task"); await editor.press("Enter");
 const queue=page.locator(".pending-send-queue"); await expect(queue.locator(".composer-chip")).toHaveCount(1);
 await expect(queue.locator(".composer-chip")).toHaveAttribute("title", /activates when sent/);
 await editor.fill("Newer draft"); await queue.getByRole("button",{name:/Steer/}).click();
 await expect(page.locator(".chat-message-user").last()).toContainText("Inspect queued task");
 await expect(page.locator(".chat-message-user").last().locator(".composer-chip")).toHaveCount(1);
 await expect(editor).toHaveText("Newer draft");
});

test("pasting an existing Skill keeps one chip and one canonical activation", async ({ page }) => {
  const { instanceId, sessionId, h } = await setup(page);
  const editor = page.getByLabel("Message", { exact: true });
  await skill(page, "review");
  await editor.pressSequentially("Inspect this");
  async function paste() {
    await editor.evaluate((el, instanceId) => {
      const data = new DataTransfer();
      data.setData("text/plain", "/Review copied");
      data.setData("application/x-agent-core-parts", JSON.stringify({
        instanceId,
        parts: [
          { kind: "invocation", invocationKind: "skill", skillKey: "instance:review", label: "Review" },
          { kind: "text", text: " copied" },
        ],
      }));
      el.dispatchEvent(new ClipboardEvent("paste", { bubbles: true, cancelable: true, clipboardData: data }));
    }, instanceId);
  }
  await paste();
  await expect(editor.locator('[data-skill-key="instance:review"]')).toHaveCount(1);
  await expect(editor).toHaveText("/Review Inspect this copied");
  // Replacing the selected chip must still allow that Skill to be pasted.
  await editor.press("ControlOrMeta+a");
  await paste();
  await expect(editor.locator('[data-skill-key="instance:review"]')).toHaveCount(1);
  await expect(editor).toHaveText("/Review copied");
  await editor.press("Enter");
  await expect(page.locator(".chat-message-user").last().locator(".composer-chip")).toHaveCount(1);
  await expect.poll(async () => {
    const response = await page.request.get(`/api/v2/sessions/${sessionId}/agent-runs`, { headers: h });
    return (await response.json()).items.find((r: { composerInput?: { explicitSkillKeys: string[] } }) =>
      r.composerInput?.explicitSkillKeys.includes("instance:review"),
    )?.composerInput.explicitSkillKeys;
  }).toEqual(["instance:review"]);
});


test("picker active option belongs to the focused editor or plus search", async ({ page }) => {
  await setup(page);
  const editor = page.getByLabel("Message", { exact: true });
  await editor.pressSequentially("/");
  await expect(page.getByRole("option").first()).toBeVisible();
  await expect(editor).toHaveAttribute("aria-controls", "composer-choices");
  await expect(editor).toHaveAttribute("aria-activedescendant", "composer-choice-0");
  await editor.press("Escape");
  await expect(editor).not.toHaveAttribute("aria-activedescendant");
  await page.getByRole("button", { name: "Add content" }).click();
  await page.getByRole("menuitem", { name: "Use Skill" }).click();
  const search = page.getByRole("combobox", { name: "Search composer choices" });
  await expect(search).toBeFocused();
  await expect(page.getByRole("option").first()).toBeVisible();
  await expect(search).toHaveAttribute("aria-controls", "composer-choices");
  await expect(search).toHaveAttribute("aria-activedescendant", "composer-choice-0");
  await expect(editor).not.toHaveAttribute("aria-activedescendant");
  await search.press("ArrowDown");
  await expect(search).toHaveAttribute("aria-activedescendant", "composer-choice-1");
  await expect(page.locator("#composer-choice-1")).toHaveAttribute("aria-selected", "true");
  await search.fill("no-such-skill-needle");
  await expect(page.getByText("No matching choices", { exact: true })).toBeVisible();
  await expect(search).not.toHaveAttribute("aria-activedescendant");
  await search.fill("instance:review");
  await expect(page.getByRole("option")).toHaveCount(1);
  await search.press("Enter");
  await expect(editor).toBeFocused();
  await expect(editor.locator('[data-skill-key="instance:review"]')).toHaveCount(1);
  await expect(editor).not.toHaveAttribute("aria-controls");
});

test("composer pickers dismiss without losing literal input or reopening after focus", async ({ page }) => {
  await setup(page);
  const editor = page.getByLabel("Message", { exact: true });
  const picker = page.getByRole("dialog", { name: "Reference resource", exact: true });
  async function open() {
    await editor.click();
    await editor.press("ControlOrMeta+a");
    await editor.press("Backspace");
    await expect(editor).toHaveText("");
    await editor.pressSequentially("@");
    await expect(picker).toBeVisible();
  }
  await open();
  await page.getByRole("heading", { name: "Alex", exact: true }).click();
  await expect(picker).toBeHidden();
  await editor.click();
  await expect(editor).toHaveText("@");
  await expect(picker).toBeHidden();
  await open();
  await editor.press("Tab");
  await expect(picker).toBeHidden();
  await open();
  await editor.evaluate(element => (element as HTMLElement).blur());
  await expect(picker).toBeHidden();
  await open();
  await page.evaluate(() => window.dispatchEvent(new Event("blur")));
  await expect(picker).toBeHidden();
  await open();
  await editor.press("Escape");
  await editor.pressSequentially("review");
  await expect(editor).toHaveText("@review");
  await expect(picker).toBeHidden();
  await page.getByRole("button", { name: "Add content" }).click();
  await page.getByRole("menuitem", { name: "Reference resource" }).click();
  const search = page.getByRole("combobox", { name: "Search composer choices" });
  await search.fill("review");
  await expect(page.getByRole("option").filter({ hasText: "instance:review" })).toBeVisible();
  await search.press("Escape");
  await expect(picker).toBeHidden();
  await expect(editor).toBeFocused();
  for (const name of ["Add content", "Model"]) {
    const popup = name === "Model" ? page.getByRole("listbox", { name: "Model catalog" }) : page.getByRole("menu");
    for (const dismissal of ["Escape", "focus", "blur", "outside"]) {
      await page.getByRole("button", { name, exact: true }).click();
      await expect(popup).toBeVisible();
      if (dismissal === "Escape") await page.keyboard.press("Escape");
      else if (dismissal === "focus") await editor.focus();
      else if (dismissal === "blur") await page.evaluate(() => window.dispatchEvent(new Event("blur")));
      else await page.getByRole("heading", { name: "Alex", exact: true }).click();
      await expect(popup).toBeHidden();
    }
  }
});

test("unified resource search keeps successful sources visible and retries a failed source", async ({ page }) => {
  await setup(page);
  let fail = true;
  let skillRequests = 0;
  await page.route("**/composer?**", async route => {
    const params = new URL(route.request().url()).searchParams;
    if (params.get("category") === "skill") skillRequests++;
    if (fail && params.get("category") === "skill")
      await route.fulfill({ status: 503, body: "Unavailable" });
    else if (params.get("category") === "session") {
      if (params.has("cursor")) await route.fulfill({ json: { items: [], nextCursor: null } });
      else {
        const response = await route.fetch();
        await route.fulfill({ json: { ...await response.json(), nextCursor: "more-chats" } });
      }
    }
    else await route.continue();
  });
  await page.getByLabel("Message", { exact: true }).pressSequentially("@");
  const picker = page.getByRole("dialog", { name: "Reference resource", exact: true });
  await expect(picker.getByRole("option").filter({ hasText: "Chat" })).toBeVisible();
  await expect(picker).toContainText("Some choices could not load");
  await expect(picker.getByRole("button", { name: "Load more", exact: true })).toHaveCount(0);
  await expect(picker).toContainText("Some choices could not load");
  await expect(picker.getByRole("button", { name: "Retry", exact: true })).toBeVisible();
  expect(skillRequests).toBe(1);
  fail = false;
  await picker.getByRole("button", { name: "Retry" }).click();
  await expect(picker.getByRole("option").filter({ hasText: "instance:review" })).toBeVisible();
  await expect(picker).not.toContainText("Some choices could not load");
  expect(skillRequests).toBe(2);
  await picker.getByRole("option").filter({ hasText: "instance:review" }).click();
  await expect(page.getByLabel("Message", { exact: true }).locator('[data-composer-chip="reference"]')).toHaveCount(1);
});


test("bounded search automatically scans empty pages before reporting no matches", async ({ page }) => {
  await setup(page);
  await page.route("**/composer?**", async route => {
    const url = new URL(route.request().url());
    if (url.searchParams.get("category") === "invocation" && url.searchParams.get("search") === "bounded-needle")
      await route.fulfill({ json: { items: [], nextCursor: url.searchParams.has("cursor") ? null : "scan-continuation" } });
    else await route.continue();
  });
  const editor = page.getByLabel("Message", { exact: true });
  await editor.pressSequentially("/bounded-needle");
  await expect(editor).not.toHaveAttribute("aria-activedescendant");
  const picker = page.getByRole("dialog", { name: "Use Skill", exact: true });
  await expect(page.getByText("No matching choices", { exact: true })).toBeVisible();
  await expect(picker.getByRole("button", { name: "Load more", exact: true })).toBeHidden();
});


for (const width of [1440, 768, 390]) {
  for (const mode of ["invocation", "reference"] as const) {
    test(`composer ${mode} auto-pages on scroll at ${width}px and recovers a failed page`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      const { instanceId } = await setup(page);
      let nextRequests = 0;
      let fail = true;
      const category = mode === "invocation" ? "invocation" : "skill";
      const choice = (i: number) => ({ id: `instance:scroll-${i}`, label: `Scroll choice ${i} with a long descriptive title`,
        description: "A long description that should truncate within the existing row", category,
        ...(mode === "invocation" ? { skillKey: `instance:scroll-${i}` }
          : { reference: { kind: "skill", agentInstanceId: instanceId, skillKey: `instance:scroll-${i}` } }) });
      await page.route("**/composer?**", async route => {
        const params = new URL(route.request().url()).searchParams;
        if (params.get("category") !== category) return route.fulfill({ json: { items: [], nextCursor: null } });
        if (!params.has("cursor")) return route.fulfill({ json: { items: Array.from({ length: 20 }, (_, i) => choice(i)), nextCursor: "page2" } });
        nextRequests++;
        if (fail) return route.fulfill({ status: 503, body: "Unavailable" });
        await route.fulfill({ json: { items: [choice(20)], nextCursor: null } });
      });
      // Exercise the focused plus search as well as the typed caret trigger.
      if (mode === "reference") {
        await page.getByRole("button", { name: "Add content", exact: true }).click();
        await page.getByRole("menuitem", { name: "Reference resource" }).click();
      } else await page.getByLabel("Message", { exact: true }).pressSequentially("/");
      const picker = page.getByRole("dialog", { name: mode === "invocation" ? "Use Skill" : "Reference resource", exact: true });
      const list = picker.getByRole("listbox");
      await expect(list.getByRole("option")).toHaveCount(20);
      expect(nextRequests).toBe(0);
      await expect(picker.getByRole("button", { name: "Load more", exact: true })).toHaveCount(0);
      const bounds = await list.boundingBox();
      await page.mouse.move(bounds!.x + bounds!.width / 2, bounds!.y + bounds!.height / 2);
      if (width === 768 && mode === "invocation") {
        for (let i = 0; i < 19; i++) await page.getByLabel("Message", { exact: true }).press("ArrowDown");
      } else await page.mouse.wheel(0, 2000);
      await expect(picker.getByRole("button", { name: "Retry", exact: true })).toBeVisible();
      expect(nextRequests).toBe(1);
      await page.mouse.wheel(0, 2000);
      expect(nextRequests).toBe(1);
      fail = false;
      await picker.getByRole("button", { name: "Retry", exact: true }).click();
      await expect(list.getByRole("option")).toHaveCount(21);
      expect(nextRequests).toBe(2);
      await expect(picker.getByRole("button", { name: "Retry", exact: true })).toHaveCount(0);
      const focus = mode === "reference" ? picker.getByRole("combobox") : page.getByLabel("Message", { exact: true });
      await expect(focus).toBeFocused();
      await page.mouse.wheel(0, 2000);
      await list.getByRole("option").last().click();
      await expect(page.getByLabel("Message", { exact: true }).locator(`[data-composer-chip="${mode === "invocation" ? "invocation" : "reference"}"]`)).toHaveCount(1);
      await expect(picker).toBeHidden();
    });
  }
}
