import { execFileSync } from "node:child_process";
import { expect, test } from "@playwright/test";

const dbPath = process.env.PLAYWRIGHT_SQLITE_PATH ?? "";

function sqlite(script: string, args: string[] = []): string {
  return execFileSync("python3", ["-c", script, dbPath, ...args], { encoding: "utf8" }).trim();
}

function latestSessionId(): string {
  return sqlite(`
import sqlite3, sys
con = sqlite3.connect(sys.argv[1], timeout=30)
con.execute("PRAGMA busy_timeout=8000")
row = con.execute(
    "SELECT SourceSessionId FROM TriggerRegistrations WHERE Intent = ? ORDER BY CreatedAtUtc DESC LIMIT 1",
    ("Call John",),
).fetchone()
con.close()
if row is None or not row[0]:
    raise SystemExit("reminder registration was not stored")
print(row[0])
`);
}

function makeReminderDue(sessionId: string): void {
  sqlite(`
import sqlite3, sys, time
db, session_id = sys.argv[1], sys.argv[2]
con = sqlite3.connect(db, timeout=30)
con.execute("PRAGMA busy_timeout=8000")
due = int(time.time() * 1000) - 60_000
updated = con.execute(
    "UPDATE TriggerRegistrations SET NextOccurrenceAtUtc=? WHERE SourceSessionId=? AND Status=0",
    (due, session_id),
).rowcount
con.commit()
con.close()
if updated != 1:
    raise SystemExit(f"expected one active registration, updated {updated}")
`, [sessionId]);
}

function seedRetry(sessionId: string): void {
  sqlite(`
import json, sqlite3, sys, time, uuid
db, session_id = sys.argv[1], sys.argv[2]
con = sqlite3.connect(db, timeout=30)
con.execute("PRAGMA busy_timeout=8000")
owner = con.execute(
    """SELECT s.AgentInstanceId, snap.ProfileId
       FROM Sessions s
       JOIN SessionSnapshots snap ON snap.SessionId = s.SessionId
       WHERE s.SessionId=?""",
    (session_id,),
).fetchone()
if owner is None or not owner[0] or not owner[1]:
    raise SystemExit("session owner was not stored")
persona = con.execute("SELECT PersonaJson FROM AgentInstances WHERE InstanceId=?", (owner[0],)).fetchone()
name = "Riley"
if persona and persona[0]:
    name = json.loads(persona[0]).get("name") or name
now = int(time.time() * 1000)
work_id = str(uuid.uuid4())
con.execute(
    """INSERT INTO WorkItems (
        WorkItemId, AgentInstanceId, ProfileId, Status, Revision, AttemptCount, MaxAttempts,
        NextRetryAtUtc, CancellationRequested, SourceOccurrenceId, SourceKind,
        SourceSessionId, DedupeKey, ObservedAtUtc, EvidenceJson, DefinitionId, DefinitionVersion,
        PersonaName, ModelCatalogKey, ModelProviderAlias, ModelId, ModelReasoningEffort,
        SideEffectDisposition, CreatedAtUtc, UpdatedAtUtc
    ) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)""",
    (
        work_id, owner[0], owner[1], 3, 2, 1, 3,
        now + 86_400_000, 0, str(uuid.uuid4()), 0,
        session_id, f"retry-{work_id}", now, "{}", "general-assistant", 10,
        name, "scripted-alpha", "primary-llm", "scripted-alpha", "medium",
        0, now, now,
    ),
)
con.commit()
con.close()
`, [sessionId]);
}

test("a detached reminder completes in Background work and cancel survives reload", async ({ page }) => {
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

  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto("/");
  await page.waitForFunction(() => window.localStorage.getItem("agent-core.owner-capability"));
  await page.evaluate(async () => {
    const token = window.localStorage.getItem("agent-core.owner-capability") ?? "";
    const headers = {
      "content-type": "application/json",
      "X-AgentCore-Owner-Capability": token
    };
    const current = await fetch("/api/v2/profile", { headers });
    if (!current.ok) {
      throw new Error(`profile ${current.status}`);
    }
    const profile = (await current.json()) as { revision: number };
    const patched = await fetch("/api/v2/profile", {
      method: "PATCH",
      headers,
      body: JSON.stringify({ expectedRevision: profile.revision, values: { timeZone: "UTC" } })
    });
    if (!patched.ok) {
      throw new Error(await patched.text());
    }
  });
  await page.getByRole("button", { name: "Start a new chat" }).click();
  await expect(page.getByRole("combobox", { name: "Identity" })).toBeEnabled({ timeout: 15_000 });
  await page.getByRole("combobox", { name: "Identity" }).click();
  await page.locator(".ant-select-item-option", { hasText: "Riley — General assistant" }).click();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });
  await page.getByLabel("Message").fill("remind me tomorrow");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Scheduled Call John.")).toBeVisible({ timeout: 15_000 });

  await page.getByRole("button", { name: "Conversation actions" }).click();
  await page.getByRole("menuitem", { name: "End" }).click();
  await expect(page.getByText("This conversation has ended.")).toBeVisible({ timeout: 15_000 });

  const sessionId = latestSessionId();
  makeReminderDue(sessionId);
  await page.getByRole("button", { name: "Background work" }).click();
  const drawer = page.getByRole("dialog", { name: "Background work" });
  await expect(drawer.getByText("Completed")).toBeVisible({ timeout: 25_000 });
  await expect(drawer.getByText("Hello from synthetic.")).toBeVisible({ timeout: 20_000 });
  const transcript = page.locator(".conversation-scroll");
  await expect(transcript).toContainText("Scheduled Call John.");
  await expect(transcript).not.toContainText("Hello from synthetic.");

  await page.reload();
  await expect(page.getByText("This conversation has ended.")).toBeVisible({ timeout: 15_000 });
  await page.getByRole("button", { name: "Background work" }).click();
  await expect(drawer.getByText("Hello from synthetic.")).toBeVisible({ timeout: 15_000 });
  await expect(page.locator(".conversation-scroll")).not.toContainText("Hello from synthetic.");

  seedRetry(sessionId);
  await page.keyboard.press("Escape");
  await page.getByRole("button", { name: "Background work" }).click();
  await expect(drawer.getByText("Retrying")).toBeVisible({ timeout: 15_000 });
  await drawer.getByRole("button", { name: "Cancel Scheduled reminder" }).click();
  await page.getByRole("button", { name: "Cancel work" }).click();
  await expect(drawer.getByText("Cancelled")).toBeVisible({ timeout: 15_000 });

  await page.reload();
  await page.getByRole("button", { name: "Background work" }).click();
  await expect(drawer.getByText("Cancelled")).toBeVisible({ timeout: 15_000 });
  await expect(drawer.getByText("Hello from synthetic.")).toBeVisible();
  await expect(page.locator(".conversation-scroll")).not.toContainText("Hello from synthetic.");

  const unexpectedConsole = consoleErrors.filter((message) => !message.includes("[antd: List]"));
  expect(unexpectedConsole).toEqual([]);
  expect(serverErrors).toEqual([]);
});
