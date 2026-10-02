import { expect, test } from "@playwright/test";
import { LEGACY_IDENTITY_LABELS, selectLegacyIdentity } from "./support/legacy-identity";

test("store connection and quiet background work stay labeled", async ({ page }) => {
  test.setTimeout(90_000);
  const consoleErrors: string[] = [];
  const serverErrors: string[] = [];
  page.on("console", (message) => {
    if (message.type() === "error") {
      consoleErrors.push(message.text());
    }
  });
  page.on("pageerror", (error) => consoleErrors.push(error.message));
  page.on("response", (response) => {
    if (response.status() >= 500) {
      serverErrors.push(`${response.status()} ${response.url()}`);
    }
  });

  await page.emulateMedia({ reducedMotion: "reduce" });
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto("/");
  await page.waitForFunction(() => window.localStorage.getItem("agent-core.owner-capability"));
  await page.getByRole("button", { name: "Start a new chat" }).click();
  await expect(page.getByRole("combobox", { name: "Identity" })).toBeEnabled({ timeout: 15_000 });
  await selectLegacyIdentity(page, LEGACY_IDENTITY_LABELS.generalAssistant);
  await page.getByLabel("Message").fill("hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });

  const work = page.getByRole("button", { name: "Background work", exact: true });
  await expect(work).toBeVisible({ timeout: 15_000 });
  await expect(page.getByRole("button", { name: /need attention/ })).toHaveCount(0);
  await work.focus();
  await expect(work).toBeFocused();
  await page.keyboard.press("Enter");
  const drawer = page.getByRole("dialog", { name: "Background work" });
  await expect(drawer.getByText("No background work yet")).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(drawer).toBeHidden();

  await expect(page.getByLabel("Store connection", { exact: true })).toContainText("Not connected");
  await page.getByRole("button", { name: "Manage store connection" }).click();
  await expect(page).toHaveURL(/\/admin\/instances\/[0-9a-f-]{36}$/i);
  const section = page.getByRole("region", { name: "Application connection" });
  await expect(section.getByRole("heading", { name: "Store connection" })).toBeVisible();
  await expect(section.getByText("Not connected")).toBeVisible();

  await section.getByLabel("Store URL").fill("not a url");
  await section.getByRole("button", { name: "Connect" }).focus();
  await expect(section.getByRole("button", { name: "Connect" })).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(section.getByText("Base URL must be an absolute http or https origin without credentials.")).toBeVisible();
  await expect(section.getByText(/cookie|token|profile path/i)).toHaveCount(0);

  await page.setViewportSize({ width: 390, height: 800 });
  await expect(section.getByRole("heading", { name: "Store connection" })).toBeVisible();
  await expect(section.getByRole("button", { name: "Connect" })).toBeVisible();

  expect(serverErrors).toEqual([]);
  const unexpected = consoleErrors.filter(
    (line) =>
      !line.includes("[antd: List]") &&
      !line.includes("404 (Not Found)") &&
      !line.includes("400 (Bad Request)"),
  );
  expect(unexpected).toEqual([]);
});
