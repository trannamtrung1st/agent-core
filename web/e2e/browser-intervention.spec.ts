import { expect, test, type Page } from "@playwright/test";
import { LEGACY_IDENTITY_LABELS, selectLegacyIdentity } from "./support/legacy-identity";
import { waitForResponseSettled } from "./support/response-settled";

const handoff = "I've reached the registration page. Please complete it in the browser and tell me to continue.";
const resumed = "The account page is ready.";

async function chooseScriptedAlpha(page: Page): Promise<void> {
  await page.getByRole("button", { name: "Model" }).click();
  await page.getByTitle("Scripted Alpha").click();
  await expect(page.locator(".model-picker")).toContainText("Scripted Alpha", { timeout: 15_000 });
}

test("signup fixture hands the browser to the user and resumes on continue", async ({ page }) => {
  test.setTimeout(180_000);
  await page.emulateMedia({ reducedMotion: "reduce" });
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto("/");
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await selectLegacyIdentity(page, LEGACY_IDENTITY_LABELS.generalAssistant);
  await chooseScriptedAlpha(page);

  await page.getByLabel("Message").fill("Please try the signup fixture.");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.locator(".chat-message-assistant .assistant-body")).toHaveText([handoff], { timeout: 120_000 });
  await waitForResponseSettled(page);
  await expect(page.getByTestId("session-failure")).toHaveCount(0);

  await page.getByLabel("Message").fill("continue");
  await page.getByRole("button", { name: "Send" }).click();
  await expect
    .poll(async () => page.locator(".chat-message-assistant .assistant-body").allTextContents(), {
      timeout: 120_000
    })
    .toEqual([handoff, resumed]);
  await waitForResponseSettled(page);
  await expect(page.getByTestId("session-failure")).toHaveCount(0);
});
