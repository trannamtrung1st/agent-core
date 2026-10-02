import { expect, test, type Page } from "@playwright/test";
import { LEGACY_IDENTITY_LABELS, selectLegacyIdentity } from "./support/legacy-identity";

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

test("muted voice keeps a browser tool turn in voice", async ({ page }) => {
  test.setTimeout(180_000);
  await page.goto("/");
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await selectLegacyIdentity(page, LEGACY_IDENTITY_LABELS.generalAssistant);
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
  await selectLegacyIdentity(page, LEGACY_IDENTITY_LABELS.customerSupport);
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
