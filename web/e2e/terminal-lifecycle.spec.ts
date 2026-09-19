import { execFileSync } from "node:child_process";
import fs from "node:fs";
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
  fs.mkdirSync(path.dirname(dbPath), { recursive: true });
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

async function postLifecycle(page: Page, sessionId: string, target: string, host = false): Promise<void> {
  const result = await page.evaluate(async ({ id, target: next, host: useHost }) => {
    const token = window.localStorage.getItem("agent-core.owner-capability");
    const path = useHost
      ? `/api/v2/host/sessions/${id}/lifecycle`
      : `/api/v2/sessions/${id}/lifecycle`;
    const response = await fetch(path, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "X-AgentCore-Owner-Capability": token ?? ""
      },
      body: JSON.stringify({ target: next })
    });
    return { ok: response.ok, status: response.status, body: await response.text() };
  }, { id: sessionId, target, host });
  expect(result.ok, `${target} ${result.status} ${result.body}`).toBe(true);
}

async function expectReadOnly(page: Page, note: string): Promise<void> {
  await expect(page.locator(".conversation-ended-note")).toHaveText(note);
  await expect(page.getByRole("button", { name: "Send" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Voice" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Resume" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Model" })).toHaveCount(0);
  await expect(page.locator(".conversation-composer [aria-label='Message']")).toHaveCount(0);
}

test("completed sessions stay read-only after reload and do not reopen", async ({ page }) => {
  const reopenUrls: string[] = [];
  page.on("request", (request) => {
    if (request.url().includes("/reopen")) {
      reopenUrls.push(request.url());
    }
  });

  const sessionId = await createSession(page);
  await postLifecycle(page, sessionId, "completed");
  await expect(page.getByTestId("connection")).toHaveText("Completed");
  await expectReadOnly(page, "This conversation is completed.");

  seedTranscript(sessionId, 80);
  await page.reload();
  await expect(page.getByTestId("connection")).toHaveText("Completed", { timeout: 15_000 });
  await expectReadOnly(page, "This conversation is completed.");
  await expect(page.getByRole("button", { name: "Load earlier messages" })).toBeVisible();
  await expect(page.getByText("History seed 80")).toBeVisible();
  await page.getByRole("button", { name: "Load earlier messages" }).click();
  await expect(page.getByText("History seed 30")).toBeVisible({ timeout: 15_000 });
  await expectReadOnly(page, "This conversation is completed.");

  await page.getByRole("button", { name: "Start a new chat" }).click();
  await expect(page.getByLabel("Identity")).toBeVisible({ timeout: 15_000 });
  const row = page.locator(".session-row").filter({ hasText: "· Completed" }).first();
  await expect(row).toBeVisible();
  reopenUrls.length = 0;
  await row.getByRole("button", { name: "Hello", exact: true }).click();
  await expect(page.getByTestId("connection")).toHaveText("Completed", { timeout: 15_000 });
  await expectReadOnly(page, "This conversation is completed.");
  expect(reopenUrls).toEqual([]);
});

test("expired and cancelled sessions stay inspectable without Resume", async ({ page }) => {
  const expiredId = await createSession(page);
  await postLifecycle(page, expiredId, "expired", true);
  await expect(page.getByTestId("connection")).toHaveText("Expired", { timeout: 15_000 });
  await expectReadOnly(page, "This conversation has expired.");
  await page.reload();
  await expect(page.getByTestId("connection")).toHaveText("Expired", { timeout: 15_000 });
  await expect(page.getByText("Hello from synthetic.")).toBeVisible();
  await expectReadOnly(page, "This conversation has expired.");

  await page.getByRole("button", { name: "Start a new chat" }).click();
  const cancelledId = await createSession(page);
  await postLifecycle(page, cancelledId, "cancelled");
  await expect(page.getByTestId("connection")).toHaveText("Cancelled", { timeout: 15_000 });
  await expectReadOnly(page, "This conversation was cancelled.");
  await page.reload();
  await expect(page.getByTestId("connection")).toHaveText("Cancelled", { timeout: 15_000 });
  await expectReadOnly(page, "This conversation was cancelled.");
});
