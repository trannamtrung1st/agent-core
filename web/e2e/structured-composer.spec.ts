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

test("two Skills and two references survive send, history reload, and pinned inspection", async ({
  page,
}) => {
  const { sessionId, h } = await setup(page);
  const editor = page.getByLabel("Message", { exact: true });
  await skill(page, "review");
  await expect(
    page.getByRole("button", { name: "Send", exact: true }),
  ).toBeDisabled();
  await skill(page, "check");
  await editor.pressSequentially("Inspect ");
  await editor.pressSequentially("@");
  await page
    .getByRole("dialog", { name: "Reference resource" })
    .getByRole("button", { name: "Chats", exact: true })
    .click();
  await expect(
    page.getByRole("option").filter({ hasText: "Hello" }),
  ).toBeVisible();
  await editor.press("Enter");
  await page.getByRole("button", { name: "Add content" }).click();
  await page.getByRole("menuitem", { name: "Reference resource" }).click();
  await page
    .getByRole("dialog", { name: "Reference resource" })
    .getByRole("button", { name: "Skills", exact: true })
    .click();
  await page.getByRole("option").filter({ hasText: "instance:review" }).click();
  await expect(editor.locator("[data-composer-chip]")).toHaveCount(4);
  await editor.press("Enter");
  await expect(
    page.locator(".chat-message-user").last().locator(".composer-chip"),
  ).toHaveCount(4);
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
 await editor.fill("Newer draft"); await queue.getByRole("button",{name:/Steer/}).click();
 await expect(page.locator(".chat-message-user").last()).toContainText("Inspect queued task");
 await expect(page.locator(".chat-message-user").last().locator(".composer-chip")).toHaveCount(1);
 await expect(editor).toHaveText("Newer draft");
});
