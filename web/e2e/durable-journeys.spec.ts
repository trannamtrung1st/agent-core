import { execFileSync } from "node:child_process";
import { expect, test, type Page } from "@playwright/test";
import { LEGACY_IDENTITY_LABELS, selectLegacyIdentity } from "./support/legacy-identity";
import { waitForResponseSettled } from "./support/response-settled";

const dbPath = process.env.PLAYWRIGHT_SQLITE_PATH ?? "";
const scheduleIntent = "Call John";
/** Exclusion id used before a new chat exists; must not match any real session. */
const noActiveSession = "00000000-0000-0000-0000-000000000000";

function sqlite(script: string, args: string[] = []): string {
  return execFileSync("python3", ["-c", script, dbPath, ...args], { encoding: "utf8" }).trim();
}

type RegistrationProbe = {
  count: number;
  registration: {
    registrationId: string;
    status: number;
    nextOccurrenceAtUtc: string | null;
    intent: string;
  } | null;
};

function probeRegistration(sessionId: string): RegistrationProbe {
  const raw = sqlite(
    `
import json, sqlite3, sys
db, session_id, intent = sys.argv[1], sys.argv[2], sys.argv[3]
con = sqlite3.connect(db, timeout=30)
con.row_factory = sqlite3.Row
con.execute("PRAGMA busy_timeout=8000")
rows = con.execute(
    """SELECT RegistrationId, Status, NextOccurrenceAtUtc, Intent
       FROM TriggerRegistrations
       WHERE SourceSessionId = ? AND Intent = ?
       ORDER BY RegistrationId""",
    (session_id, intent),
).fetchall()
con.close()
registration = None
if len(rows) == 1:
    row = rows[0]
    registration = {
        "registrationId": row["RegistrationId"],
        "status": row["Status"],
        "nextOccurrenceAtUtc": row["NextOccurrenceAtUtc"],
        "intent": row["Intent"],
    }
print(json.dumps({"count": len(rows), "registration": registration}))
`,
    [sessionId, scheduleIntent]
  );
  return JSON.parse(raw) as RegistrationProbe;
}

async function collectHarnessDiagnostics(page: Page, sessionId: string): Promise<string> {
  const connection = await page.getByTestId("connection").innerText().catch(() => "(missing)");
  const stopCount = await page.getByRole("button", { name: "Stop" }).count();
  const sendDisabled = await page.getByRole("button", { name: "Send" }).isDisabled().catch(() => true);
  const transcript = await page.locator(".conversation-scroll").innerText().catch(() => "");
  const registration = probeRegistration(sessionId);
  return [
    `sessionId=${sessionId}`,
    `connection=${connection}`,
    `stopButtonCount=${stopCount}`,
    `sendDisabled=${sendDisabled}`,
    `registration=${JSON.stringify(registration)}`,
    `transcript=${transcript}`
  ].join("\n");
}

function makeReminderDue(sessionId: string): void {
  sqlite(
    `
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
`,
    [sessionId]
  );
}

async function releaseOtherLiveRuntimes(page: Page, sessionId: string): Promise<void> {
  await page.evaluate(async (currentId) => {
    const token = window.localStorage.getItem("agent-core.owner-capability") ?? "";
    const headers = {
      "content-type": "application/json",
      "X-AgentCore-Owner-Capability": token
    };
    const listed = await fetch("/api/v2/sessions?limit=50", { headers });
    if (!listed.ok) {
      throw new Error(`catalog ${listed.status}`);
    }

    const body = (await listed.json()) as { items: { sessionId: string; status: string }[] };
    for (const item of body.items) {
      if (item.sessionId === currentId || item.status !== "attached") {
        continue;
      }

      const deactivated = await fetch(`/api/v2/sessions/${item.sessionId}/deactivate`, {
        method: "POST",
        headers
      });
      if (!deactivated.ok) {
        throw new Error(`deactivate ${item.sessionId} ${deactivated.status}`);
      }
    }
  }, sessionId);
}

function seedRetry(sessionId: string): void {
  sqlite(
    `
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
`,
    [sessionId]
  );
}

test("a detached reminder completes in Background work and cancel survives reload", async ({ page }) => {
  test.setTimeout(120_000);
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
  // Catalog cleanup before a new chat: the /c/<id> path appears only after the first send.
  await releaseOtherLiveRuntimes(page, noActiveSession);
  await page.getByRole("button", { name: "Start a new chat" }).click();
  await expect(page.getByRole("combobox", { name: "Identity" })).toBeEnabled({ timeout: 15_000 });
  await selectLegacyIdentity(page, LEGACY_IDENTITY_LABELS.generalAssistant);
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });

  const transcript = page.locator(".conversation-scroll");
  await page.getByLabel("Message").fill("remind me tomorrow");
  await page.getByRole("button", { name: "Send" }).click();

  await expect(page).toHaveURL(/\/c\/[0-9a-f-]{36}$/i, { timeout: 30_000 });
  const sessionId = page.url().match(/\/c\/([0-9a-f-]{36})/i)?.[1];
  expect(sessionId).toBeTruthy();

  let lastDiagnostics = await collectHarnessDiagnostics(page, sessionId);
  try {
    await expect
      .poll(
        async () => {
          const registration = probeRegistration(sessionId);
          const transcriptText = await transcript.innerText();
          const connection = await page.getByTestId("connection").innerText();
          const stopCount = await page.getByRole("button", { name: "Stop" }).count();
          const settled =
            connection === "Ready"
            && stopCount === 0
            && (await page.getByText("Finalizing response…").count()) === 0
            && (await page.locator(".agent-activity").count()) === 0;
          const ready =
            registration.count === 1
            && registration.registration?.intent === scheduleIntent
            && /Scheduled/i.test(transcriptText)
            && settled;
          lastDiagnostics = await collectHarnessDiagnostics(page, sessionId);
          return ready;
        },
        {
          timeout: 60_000,
          intervals: [500, 1_000, 2_000],
          message: () => lastDiagnostics
        }
      )
      .toBe(true);
  } catch (error) {
    throw new Error(`${error instanceof Error ? error.message : String(error)}\n${lastDiagnostics}`);
  }

  await waitForResponseSettled(page);

  await page.getByRole("button", { name: "Conversation actions" }).click();
  await page.getByRole("menuitem", { name: "End" }).click();
  await expect(page.getByText("This conversation has ended.")).toBeVisible({ timeout: 15_000 });

  await releaseOtherLiveRuntimes(page, sessionId);
  makeReminderDue(sessionId);
  await page.getByRole("button", { name: "Background work" }).click();
  const drawer = page.getByRole("dialog", { name: "Background work" });
  await expect(drawer.getByText("Reminder: Call John.").first()).toBeVisible({ timeout: 25_000 });
  await expect(transcript).toContainText(/Scheduled/i);
  await expect(transcript).not.toContainText("Reminder: Call John.");

  await page.reload();
  await expect(page.getByText("This conversation has ended.")).toBeVisible({ timeout: 15_000 });
  await page.getByRole("button", { name: "Background work" }).click();
  await expect(drawer.getByText("Reminder: Call John.").first()).toBeVisible({ timeout: 15_000 });
  await expect(page.locator(".conversation-scroll")).not.toContainText("Reminder: Call John.");

  seedRetry(sessionId);
  await page.keyboard.press("Escape");
  await page.getByRole("button", { name: "Background work" }).click();
  await expect(drawer.getByText("Retrying").first()).toBeVisible({ timeout: 15_000 });
  await drawer
    .locator(".background-work-item", { hasText: "Retrying" })
    .first()
    .getByRole("button", { name: "Cancel Scheduled reminder" })
    .click();
  await page.getByRole("button", { name: "Cancel work" }).click();
  await expect(drawer.getByText("Cancelled").first()).toBeVisible({ timeout: 15_000 });

  await page.reload();
  await page.getByRole("button", { name: "Background work" }).click();
  await expect(drawer.getByText("Cancelled").first()).toBeVisible({ timeout: 15_000 });
  await expect(drawer.getByText("Reminder: Call John.").first()).toBeVisible();
  await expect(page.locator(".conversation-scroll")).not.toContainText("Reminder: Call John.");

  const unexpectedConsole = consoleErrors.filter((message) => !message.includes("[antd: List]"));
  expect(unexpectedConsole).toEqual([]);
  expect(serverErrors).toEqual([]);
});
