import { execFileSync } from "node:child_process";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { expect, test, type Page } from "@playwright/test";

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const dbPath = process.env.PLAYWRIGHT_SQLITE_PATH
  ?? path.join(repoRoot, "data", "agent-core.db");

function seedTranscript(sessionId: string, lastSequence: number): void {
  const script = `
import sqlite3, sys, time, uuid
session_id, target, db = sys.argv[1], int(sys.argv[2]), sys.argv[3]
con = sqlite3.connect(db, timeout=30)
con.execute("PRAGMA busy_timeout=8000")
max_seq = con.execute(
    "SELECT COALESCE(MAX(EntrySequence), 0) FROM ConversationEntries WHERE SessionId=?",
    (session_id,),
).fetchone()[0]
now = int(time.time() * 1000)
rows = []
for seq in range(int(max_seq) + 1, target + 1):
    entry_id = str(uuid.uuid4())
    role = "User" if seq % 2 == 1 else "Assistant"
    text = f"History seed {seq}"
    source = entry_id if role == "User" else None
    response = None if role == "User" else str(uuid.uuid4())
    rows.append((entry_id, session_id, seq, source, role, text, response, "Completed", "Text", len(text), len(text), now))
con.executemany(
    """INSERT INTO ConversationEntries (
        EntryId, SessionId, EntrySequence, SourceEventId, Role, Text, ResponseId,
        Status, DeliveryMode, HeardTextEndExclusive, ReceivedTextEndExclusive, CreatedAtUtc
    ) VALUES (?,?,?,?,?,?,?,?,?,?,?,?)""",
    rows,
)
con.execute("UPDATE SessionSnapshots SET LastEntrySequence=? WHERE SessionId=?", (target, session_id))
con.commit()
con.close()
`;
  execFileSync("python3", ["-c", script, sessionId, String(lastSequence), dbPath], {
    stdio: "pipe"
  });
}

async function createSession(page: Page): Promise<string> {
  await page.goto("/");
  await page.getByLabel("Message").fill("Hello");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Hello from synthetic.")).toBeVisible({ timeout: 15_000 });
  await expect(page).toHaveURL(/\/c\/[0-9a-f-]{36}$/i);
  return new URL(page.url()).pathname.split("/").pop()!;
}

async function openSeededSession(page: Page, sessionId: string): Promise<void> {
  await page.goto(`/c/${sessionId}`);
}

test("long session opens on the newest page and load-older keeps the anchor", async ({ page }) => {
  const historyUrls: string[] = [];
  page.on("request", (request) => {
    if (request.url().includes("/messages")) {
      historyUrls.push(request.url());
    }
  });

  const sessionId = await createSession(page);
  await page.getByRole("button", { name: "Start a new chat" }).click();
  await expect(page.getByLabel("Identity")).toBeVisible({ timeout: 15_000 });
  seedTranscript(sessionId, 500);
  historyUrls.length = 0;
  await openSeededSession(page, sessionId);
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await expect(page.getByText("History seed 500")).toBeVisible({ timeout: 15_000 });
  await expect(page.getByText("History seed 1")).toHaveCount(0);

  const newest = historyUrls.filter((url) => {
    const parsed = new URL(url);
    return parsed.pathname.includes("/messages")
      && !parsed.searchParams.has("after")
      && !parsed.searchParams.has("before");
  });
  expect(newest.length).toBeGreaterThan(0);
  expect(newest.every((url) => new URL(url).searchParams.get("limit") === "50")).toBe(true);
  expect(historyUrls.some((url) => new URL(url).searchParams.has("after"))).toBe(false);

  const scroller = page.locator(".conversation-scroll");
  const beforeFirst = await scroller.evaluate((element) => ({
    top: element.scrollTop,
    height: element.scrollHeight
  }));
  await page.getByRole("button", { name: "Load earlier messages" }).click();
  await expect(page.getByText("History seed 450")).toBeVisible({ timeout: 15_000 });
  const afterFirst = await scroller.evaluate((element) => ({
    top: element.scrollTop,
    height: element.scrollHeight
  }));
  expect(afterFirst.height).toBeGreaterThan(beforeFirst.height);
  expect(afterFirst.top).toBeGreaterThanOrEqual(beforeFirst.top);

  await page.getByRole("button", { name: "Load earlier messages" }).click();
  await expect(page.getByText("History seed 400")).toBeVisible({ timeout: 15_000 });
  await expect(page.getByText("History seed 500")).toBeVisible();

  let releaseOlder: (() => void) | undefined;
  const delayed = new Promise<void>((resolve) => {
    releaseOlder = resolve;
  });
  await page.route("**/api/v1/sessions/*/messages?*before=*", async (route) => {
    await delayed;
    await route.continue();
  });
  const loadOlder = page.getByRole("button", { name: "Load earlier messages" }).click();
  await page.getByRole("textbox", { name: "Message" }).fill("Live during older fetch");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Live during older fetch")).toBeVisible({ timeout: 15_000 });
  releaseOlder?.();
  await loadOlder;
  await expect(page.getByText("History seed 350")).toBeVisible({ timeout: 15_000 });
  await expect(page.getByText("Live during older fetch")).toBeVisible();
});

test("switching session ignores a delayed older-history page", async ({ page }) => {
  test.setTimeout(60_000);
  const firstId = await createSession(page);
  await page.getByRole("button", { name: "Start a new chat" }).click();
  seedTranscript(firstId, 120);
  await openSeededSession(page, firstId);
  await expect(page.getByRole("button", { name: "Load earlier messages" })).toBeVisible({ timeout: 15_000 });

  let releaseOlder: (() => void) | undefined;
  const delayed = new Promise<void>((resolve) => {
    releaseOlder = resolve;
  });
  await page.route("**/api/v1/sessions/*/messages?*before=*", async (route) => {
    await delayed;
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        items: [{
          entryId: "00000000-0000-4000-8000-000000000070",
          sequence: 70,
          sourceEventId: "00000000-0000-4000-8000-000000000070",
          role: "user",
          text: "History seed 70",
          responseId: null,
          status: "completed",
          deliveryMode: "text",
          heardTextEndExclusive: 15,
          receivedTextEndExclusive: 15,
          createdAt: "2026-01-01T00:00:00Z"
        }],
        nextAfter: 70,
        hasMore: false,
        hasOlder: true,
        nextBefore: 70
      })
    });
  });
  void page.getByRole("button", { name: "Load earlier messages" }).click({ noWaitAfter: true }).catch(() => undefined);
  await page.getByRole("button", { name: "Start a new chat" }).click();
  await expect(page.getByLabel("Identity")).toBeVisible({ timeout: 15_000 });
  releaseOlder?.();
  await expect(page.getByText("History seed 70", { exact: true })).toHaveCount(0);
});

test("terminal read-only sessions load older pages", async ({ page }) => {
  const sessionId = await createSession(page);
  await page.getByRole("button", { name: "Conversation actions" }).click();
  await page.getByRole("menuitem", { name: "End" }).click();
  await expect(page.getByText("This conversation has ended.")).toBeVisible({ timeout: 15_000 });
  seedTranscript(sessionId, 160);
  await page.reload();
  await expect(page.getByText("This conversation has ended.")).toBeVisible({ timeout: 15_000 });
  await expect(page.getByRole("button", { name: "Load earlier messages" })).toBeVisible();
  await expect(page.getByText("History seed 160")).toBeVisible();
  await page.getByRole("button", { name: "Load earlier messages" }).click();
  await expect(page.getByText("History seed 110")).toBeVisible({ timeout: 15_000 });
  await expect(page.getByRole("button", { name: "Send" })).toHaveCount(0);
});
