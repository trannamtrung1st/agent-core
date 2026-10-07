import { expect, test } from "@playwright/test";

test("approval decisions fit short viewports and replacement proposals start at the top", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByTestId("connection")).toHaveText("Ready");
  // Presentation fixture only. Server-authorized decisions are exercised in approval-flow and p97-harness-management.
  await page.evaluate(async () => {
    const modulePath = "/src/state/sessionStore.ts";
    const { useSessionStore } = await import(modulePath);
    useSessionStore.setState({ pendingApproval: {
      approvalId: "layout-first", responseId: "layout", operationId: "layout",
      toolName: "harness.instructions.update", effect: "write",
      summary: "Save instructions.update for future conversations", expiresAt: "2099-01-01T00:00:00Z",
      details: {
        Change: "Opening line\n\n" + "Review section:\n- Read the complete proposal before deciding.\n\n".repeat(20),
        "Applies to": "Future conversations; this Session stays pinned.",
        "Active version": "16", "Policy revision": "2",
        Limitations: "External production outcomes remain unverified."
      }
    } });
  });
  const dialog = page.getByRole("dialog", { name: "Save this harness change?" });
  const review = dialog.getByRole("region", { name: "Approval details" });
  await expect(dialog).toBeVisible();

  for (const [width, height] of [[1440, 900], [768, 900], [390, 844], [390, 568], [844, 390]]) {
    await page.setViewportSize({ width, height });
    await expect.poll(async () => dialog.evaluate(element => {
      const footer = element.querySelector(".ant-modal-footer")!.getBoundingClientRect();
      return footer.top >= 0 && footer.bottom <= innerHeight;
    })).toBe(true);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await expect(dialog.getByText("Future conversations; this Session stays pinned.")).toBeVisible();
    if (width < 768) {
      await expect.poll(async () => (await dialog.getByRole("button", { name: "Approve", exact: true }).boundingBox())!.height).toBeGreaterThanOrEqual(40);
    }
  }

  await review.focus();
  await page.keyboard.press("End");
  await expect.poll(() => review.evaluate(element => element.scrollTop)).toBeGreaterThan(0);
  await page.evaluate(async () => {
    const modulePath = "/src/state/sessionStore.ts";
    const { useSessionStore } = await import(modulePath);
    const previous = useSessionStore.getState().pendingApproval;
    useSessionStore.setState({ pendingApproval: {
      ...previous, approvalId: "layout-replacement",
      details: { ...previous.details, Change: "Replacement opening line\n" + previous.details.Change }
    } });
  });
  await expect.poll(() => review.evaluate(element => element.scrollTop)).toBe(0);
  await expect(review.getByText("Replacement opening line", { exact: true })).toBeVisible();

  // Oversized context must remain scrollable rather than squeezing the proposal to zero height.
  await page.setViewportSize({ width: 390, height: 568 });
  await page.evaluate(async () => {
    const modulePath = "/src/state/sessionStore.ts";
    const { useSessionStore } = await import(modulePath);
    const previous = useSessionStore.getState().pendingApproval;
    useSessionStore.setState({ pendingApproval: {
      ...previous, summary: "A complete bounded approval summary. ".repeat(8),
      details: { ...previous.details, "Applies to": "Future-conversation scope remains available. ".repeat(8) }
    } });
  });
  expect((await review.boundingBox())!.height).toBeGreaterThanOrEqual(32);
  const body = dialog.locator(".ant-modal-body");
  expect(await body.evaluate(element => element.scrollHeight > element.clientHeight)).toBe(true);
  await body.evaluate(element => element.scrollTop = element.scrollHeight);
  await review.getByText("Replacement opening line", { exact: true }).scrollIntoViewIfNeeded();
  await expect(review.getByText("Replacement opening line", { exact: true })).toBeInViewport();
  expect(await dialog.locator(".ant-modal-footer").evaluate(element => element.getBoundingClientRect().bottom <= innerHeight)).toBe(true);
});
