import { expect, test, type Page } from "@playwright/test";
import { LEGACY_IDENTITY_LABELS, selectLegacyIdentity } from "./support/legacy-identity";
import { waitForResponseSettled } from "./support/response-settled";

const prompt = "Please look up record AC-1042.";
const applicationMessage = "I found the record. I'm checking the details now.";
const answer = "AC-1042 is In review.";

async function chooseScriptedAlpha(page: Page): Promise<void> {
  await page.getByRole("button", { name: "Model" }).click();
  await page.getByTitle("Scripted Alpha").click();
  await expect(page.locator(".model-picker")).toContainText("Scripted Alpha", { timeout: 15_000 });
}

test("P9 looks up record AC-1042 with one application message and one answer", async ({ page }) => {
  test.setTimeout(180_000);
  await page.emulateMedia({ reducedMotion: "reduce" });
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto("/");
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await selectLegacyIdentity(page, LEGACY_IDENTITY_LABELS.generalAssistant);
  await chooseScriptedAlpha(page);

  await page.evaluate(() => {
    const seen = { value: false };
    (window as Window & { __agentCoreBrowserProgress?: { value: boolean } }).__agentCoreBrowserProgress = seen;
    const observe = () => {
      const text = document.querySelector(".agent-activity")?.textContent ?? "";
      if (text.includes("Using browser…")) {
        seen.value = true;
      }
    };
    observe();
    new MutationObserver(observe).observe(document.body, {
      subtree: true,
      childList: true,
      characterData: true
    });
  });

  await page.getByLabel("Message").fill(prompt);
  await page.getByRole("button", { name: "Send" }).click();

  const application = page.locator(".chat-message-application");
  await expect(application).toHaveCount(1, { timeout: 120_000 });
  await expect(application).toContainText(applicationMessage);
  await expect
    .poll(
      async () => page.locator(".chat-message-assistant .assistant-body").allTextContents(),
      { timeout: 120_000, intervals: [500, 1_000, 2_000] }
    )
    .toEqual([answer]);
  await waitForResponseSettled(page);
  await expect(application).toHaveCount(1);
  await expect(application).toContainText(applicationMessage);
  await expect(page.locator(".chat-message-assistant .assistant-body")).toHaveText([answer]);
  await expect
    .poll(
      async () =>
        page.evaluate(
          () =>
            (window as Window & { __agentCoreBrowserProgress?: { value: boolean } }).__agentCoreBrowserProgress
              ?.value === true
        ),
      { timeout: 5_000 }
    )
    .toBe(true);
  await expect(page.locator(".agent-activity")).toHaveCount(0);
  const history = page.locator(".chat-message-user, .chat-message-assistant, .chat-message-application");
  const count = await history.count();
  expect(count).toBe(3);
  for (let index = 0; index < count; index += 1) {
    await expect(history.nth(index)).not.toContainText("Using browser…");
  }
});
