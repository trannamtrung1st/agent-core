import { expect, test } from "@playwright/test";

test("Drive review keeps complete IDs readable across viewports", async ({ page }, testInfo) => {
  const errors: string[] = [];
  const failedRequests: string[] = [];
  page.on("pageerror", error => errors.push(error.message));
  page.on("requestfailed", request => failedRequests.push(request.url()));
  await page.goto("/");
  await expect(page.getByTestId("connection")).toHaveText("Ready");
  // Presentation only; drive-office exercises server-authorized rejection and approval.
  await page.evaluate(async () => {
    const modulePath = "/src/state/sessionStore.ts";
    const { useSessionStore } = await import(modulePath);
    useSessionStore.setState({ pendingApproval: {
      approvalId: "drive-layout", responseId: "layout", operationId: "layout",
      toolName: "drive.trash_batch", effect: "write",
      summary: "Move 7 listed app-authorized Drive files to Trash?", expiresAt: "2099-01-01T00:00:00Z",
      details: {
        Files: JSON.stringify(Array.from({ length: 7 }, (_, index) => ({
          name: `sample_${index}.xlsx`, fileId: `1rbrAlYs9QfduWMP0UnhQ0RunZRbby7pa${index}`, expectedVersion: "3"
        }))),
        Limitations: "Exact IDs and versions. Execution rechecks before each effect.",
        Action: "Can be restored from Google Drive Trash."
      }
    } });
  });
  const dialog = page.getByRole("dialog", { name: /Move \d+ listed Drive files to Trash\?/ });
  const review = dialog.getByRole("region", { name: "Approval details" });
  const table = dialog.getByRole("table", { name: "Drive files in this request" });
  await expect(dialog).toBeVisible();
  for (const [width, height] of [[1440, 900], [794, 809], [768, 900], [390, 844], [320, 568], [844, 390]]) {
    await page.setViewportSize({ width, height });
    await expect.poll(() => dialog.evaluate(element => {
      const footer = element.querySelector(".ant-modal-footer")!.getBoundingClientRect();
      return footer.top >= 0 && footer.bottom <= innerHeight;
    })).toBe(true);
    expect(await review.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await expect(table.locator("tbody tr")).toHaveCount(7);
    const id = table.locator(".chat-approval-drive-file-id").first();
    await expect(id).toContainText("1rbrAlYs9QfduWMP0UnhQ0RunZRbby7pa0");
    if (width >= 768) {
      await expect(table.getByRole("columnheader", { name: "File ID", exact: true })).toBeVisible();
      await expect.poll(() => id.evaluate(element => {
        const range = document.createRange();
        range.selectNodeContents(element);
        return range.getClientRects().length;
      })).toBe(1);
    } else {
      await expect(table.getByRole("columnheader", { name: "File ID", exact: true })).toHaveCount(0);
      await expect(id).toContainText("ID: ");
    }
    if (width === 1440) await expect.poll(async () => (await dialog.boundingBox())!.width).toBe(880);
    if ([1440, 794, 390].includes(width)) {
      await page.screenshot({ path: testInfo.outputPath(`drive-review-${width}.png`) });
    }
  }
  await page.setViewportSize({ width: 320, height: 568 });
  await page.evaluate(async () => {
    const modulePath = "/src/state/sessionStore.ts";
    const { useSessionStore } = await import(modulePath);
    const previous = useSessionStore.getState().pendingApproval;
    useSessionStore.setState({ pendingApproval: { ...previous, approvalId: "drive-layout-long", details: {
      ...previous.details,
      Files: JSON.stringify(Array.from({ length: 20 }, (_, index) => ({
        name: `${"Long filename ".repeat(18)}${index}`,
        fileId: `${"a".repeat(158)}${String(index).padStart(2, "0")}`,
        expectedVersion: "9".repeat(64)
      })))
    } } });
  });
  await expect(table.locator("tbody tr")).toHaveCount(20);
  expect(await review.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
  await review.focus();
  await page.keyboard.press("End");
  await expect.poll(() => review.evaluate(element => element.scrollTop)).toBeGreaterThan(0);
  await expect(dialog.getByRole("button", { name: "Move 20 files to Trash", exact: true })).toBeInViewport();
  expect(errors).toEqual([]);
  expect(failedRequests).toEqual([]);
});

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
