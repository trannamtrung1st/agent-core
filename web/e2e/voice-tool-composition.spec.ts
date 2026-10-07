import { expect, test, type Page } from "@playwright/test";
import { INSTANCE_DEFINITIONS, selectInstanceIdentity } from "./support/instance-identity";

async function chooseScriptedAlpha(page: Page): Promise<void> {
  await page.getByRole("button", { name: "Model" }).click();
  await page.getByTitle("Scripted Alpha").click();
  await expect(page.locator(".model-picker")).toContainText("Scripted Alpha", { timeout: 15_000 });
}

async function enterMutedVoice(page: Page): Promise<void> {
  await page.getByRole("button", { name: /^Voice$/ }).click();
  await expect(page.getByTestId("connection")).toHaveText("Listening…", { timeout: 15_000 });
  await page.getByRole("button", { name: "Mute" }).click();
  await expect(page.getByRole("button", { name: "Unmute" })).toBeVisible({ timeout: 10_000 });
  await expect(page.getByRole("button", { name: "Voice" })).toHaveAttribute("aria-pressed", "true");
}

// store-connection.spec.ts revokes the shared general-assistant connection.
// A revoked row forbids later browser tools, so this journey restores Connected
// when an earlier test left the instance unusable. An absent connection stays absent.
async function allowGeneralAssistantBrowser(page: Page): Promise<void> {
  await page.waitForFunction(() => window.localStorage.getItem("agent-core.owner-capability"));
  const status = await page.evaluate(async () => {
    const token = window.localStorage.getItem("agent-core.owner-capability") ?? "";
    const headers = {
      "Content-Type": "application/json",
      "X-AgentCore-Owner-Capability": token
    };
    const listed = await fetch("/api/v2/admin/instances", { headers });
    if (!listed.ok) {
      return `instances-${listed.status}`;
    }

    const instances = (await listed.json()) as {
      items?: Array<{ instanceId: string; definitionId: string; lifecycle?: string }>;
    };
    const instance = instances.items?.find(
      (item) => item.definitionId === "general-assistant" && item.lifecycle === "Active"
    );
    if (!instance) {
      return "missing-instance";
    }

    const current = await fetch(`/api/v2/admin/agent-instances/${instance.instanceId}/connection`, { headers });
    if (!current.ok) {
      return `connection-${current.status}`;
    }

    const connection = (await current.json()) as { connectionId?: string; status?: string } | null;
    if (!connection?.connectionId || connection.status === "Connected") {
      return connection?.status ?? "absent";
    }

    const restored = await fetch(`/api/v2/admin/agent-instances/${instance.instanceId}/connection/connect`, {
      method: "POST",
      headers,
      body: JSON.stringify({ displayName: "nopCommerce", baseUrl: "http://127.0.0.1:5091" })
    });
    if (!restored.ok) {
      return `connect-${restored.status}`;
    }

    const body = (await restored.json()) as { status?: string };
    return body.status ?? "unknown";
  });
  expect(status === "absent" || status === "Connected").toBe(true);
}

test("muted voice keeps a browser tool turn in voice", async ({ page }) => {
  test.setTimeout(240_000);
  await page.goto("/");
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.generalAssistant);
  await allowGeneralAssistantBrowser(page);
  await chooseScriptedAlpha(page);
  await enterMutedVoice(page);
  await page.getByLabel("Message").fill("Please look up record AC-1042.");
  await page.getByRole("button", { name: "Send" }).click();
  const application = page.locator(".chat-message-application");
  await expect(application).toHaveCount(1, { timeout: 120_000 });
  await expect(application).toContainText("I found the record. I'm checking the details now.");
  await expect
    .poll(
      async () => page.locator(".chat-message-assistant .assistant-body").allTextContents(),
      { timeout: 120_000, intervals: [500, 1_000, 2_000] }
    )
    .toEqual(["AC-1042 is In review."]);
  await expect(page.getByRole("button", { name: "Stop" })).toHaveCount(0, { timeout: 60_000 });
  await expect(page.getByText("Mode changed")).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Voice" })).toHaveAttribute("aria-pressed", "true");
  await expect(page.getByRole("button", { name: "Unmute" })).toBeVisible();
});

test("muted voice keeps a knowledge tool turn in voice", async ({ page }) => {
  test.setTimeout(180_000);
  await page.goto("/");
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.customerSupport);
  await chooseScriptedAlpha(page);
  await enterMutedVoice(page);
  await page.getByLabel("Message").fill("Run the support case for order 91.");
  await page.getByRole("button", { name: "Send" }).click();
  await expect
    .poll(
      async () => {
        const texts = await page.locator(".chat-message-assistant .assistant-body").allTextContents();
        return texts.some((text) => /Order 91 is delayed/i.test(text));
      },
      { timeout: 120_000, intervals: [500, 1_000, 2_000] }
    )
    .toBe(true);
  await expect(page.getByText("Mode changed")).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Voice" })).toHaveAttribute("aria-pressed", "true");
  await expect(page.getByRole("button", { name: "Unmute" })).toBeVisible();
});
