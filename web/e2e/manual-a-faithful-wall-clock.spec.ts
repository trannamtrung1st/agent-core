import { execFileSync } from "node:child_process";
import { expect, test } from "@playwright/test";
import { INSTANCE_DEFINITIONS, selectInstanceIdentity } from "./support/instance-identity";

const dbPath = process.env.PLAYWRIGHT_SQLITE_PATH ?? "";
const scheduleIntent = "check the oven";
const pollDeadlineMs = 150_000;

type DurableProbe = {
  registration: {
    automationId: string;
    status: number;
    nextOccurrenceAtUtc: string | null;
    intent: string;
  } | null;
  occurrences: Array<{
    occurrenceId: string;
    disposition: number;
    scheduledAtUtc: string | null;
    acceptedAgentRunId: string | null;
  }>;
  agentRuns: Array<{
    agentRunId: string;
    status: number;
    resultText: string | null;
    sourceOccurrenceId: string | null;
  }>;
  utcNow: string;
  satisfied: boolean;
};

function sqliteJson(script: string, args: string[] = []): DurableProbe {
  const raw = execFileSync("python3", ["-c", script, dbPath, ...args], { encoding: "utf8" }).trim();
  return JSON.parse(raw) as DurableProbe;
}

function formatDiagnostics(probe: DurableProbe, sessionId: string): string {
  const lines = [
    `sessionId=${sessionId}`,
    `utcNow=${probe.utcNow}`,
    `registration=${probe.registration ? JSON.stringify(probe.registration) : "null"}`,
    `occurrences=${JSON.stringify(probe.occurrences)}`,
    `agentRuns=${JSON.stringify(probe.agentRuns)}`
  ];
  return lines.join("\n");
}

function probeDurableState(sessionId: string): DurableProbe {
  return sqliteJson(
    `
import json, sqlite3, sys
from datetime import datetime, timezone

db, session_id, intent = sys.argv[1], sys.argv[2], sys.argv[3]
accepted_durable = 6
run_completed = 4

con = sqlite3.connect(db, timeout=30)
con.row_factory = sqlite3.Row
con.execute("PRAGMA busy_timeout=8000")

reg_row = con.execute(
    """SELECT AutomationId, Status, NextOccurrenceAtUtc, Instructions
       FROM Automations
       WHERE SourceSessionId = ? AND Instructions = ?
       ORDER BY AutomationId
       LIMIT 2""",
    (session_id, intent),
).fetchall()

registration = None
occurrences = []
agent_runs = []

if len(reg_row) == 1:
    row = reg_row[0]
    registration = {
        "automationId": row["AutomationId"],
        "status": row["Status"],
        "nextOccurrenceAtUtc": row["NextOccurrenceAtUtc"],
        "intent": row["Instructions"],
    }
    reg_id = row["AutomationId"]
    occ_rows = con.execute(
        """SELECT OccurrenceId, Disposition, ScheduledAtUtc, AcceptedAgentRunId
           FROM TriggerOccurrences
           WHERE AutomationId = ?
           ORDER BY OccurrenceId""",
        (reg_id,),
    ).fetchall()
    occurrences = [
        {
            "occurrenceId": o["OccurrenceId"],
            "disposition": o["Disposition"],
            "scheduledAtUtc": o["ScheduledAtUtc"],
            "acceptedAgentRunId": o["AcceptedAgentRunId"],
        }
        for o in occ_rows
    ]
    run_rows = con.execute(
        """SELECT r.AgentRunId, r.Status, r.PayloadJson, o.OccurrenceId
           FROM AgentRuns r JOIN TriggerOccurrences o ON o.AcceptedAgentRunId=r.AgentRunId
           WHERE o.AutomationId = ?
           ORDER BY r.AgentRunId""",
        (reg_id,),
    ).fetchall()
    agent_runs = [
        {
            "agentRunId": w["AgentRunId"],
            "status": w["Status"],
            "resultText": (json.loads(w["PayloadJson"]).get("result") or {}).get("text"),
            "sourceOccurrenceId": w["OccurrenceId"],
        }
        for w in run_rows
    ]

con.close()

accepted = [o for o in occurrences if o["disposition"] == accepted_durable]
completed = [w for w in agent_runs if w["status"] == run_completed]
satisfied = bool(
    registration is not None
    and len(occurrences) == 1
    and len(accepted) == 1
    and len(agent_runs) == 1
    and len(completed) == 1
    and completed[0]["resultText"]
)

print(json.dumps({
    "registration": registration,
    "occurrences": occurrences,
    "agentRuns": agent_runs,
    "utcNow": datetime.now(timezone.utc).isoformat(),
    "satisfied": satisfied,
}))
`,
    [sessionId, scheduleIntent]
  );
}

test("MANUAL_A faithful wall-clock detached reminder", async ({ page }) => {
  test.setTimeout(270_000);

  await page.goto("/");
  await page.waitForFunction(() => window.localStorage.getItem("agent-core.owner-capability"));
  await page.getByRole("button", { name: "Start a new chat" }).click();
  await selectInstanceIdentity(page, INSTANCE_DEFINITIONS.generalAssistant);
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });

  await page.getByLabel("Message").fill("check the oven in 1 minute for me");
  await page.getByRole("button", { name: "Send" }).click();
  const transcript = page.locator(".conversation-scroll");
  await expect(transcript.getByText(/Scheduled/i)).toBeVisible({ timeout: 20_000 });
  await expect(page).toHaveURL(/\/c\/[0-9a-f-]{36}$/i, { timeout: 20_000 });
  const sessionId = page.url().match(/\/c\/([0-9a-f-]{36})/i)?.[1];
  expect(sessionId).toBeTruthy();

  await page.getByRole("button", { name: "End", exact: true }).click();
  await page.getByRole("dialog").filter({ hasText: "End this conversation?" })
    .getByRole("button", { name: "End", exact: true })
    .click();
  await expect(page.getByText("This conversation has ended.")).toBeVisible({ timeout: 15_000 });
  const transcriptBefore = await page.locator(".conversation-scroll").innerText();
  const helloBefore = (transcriptBefore.match(/Hello from synthetic/g) ?? []).length;

  let lastProbe = probeDurableState(sessionId);
  try {
    await expect
      .poll(
        () => {
          lastProbe = probeDurableState(sessionId);
          return lastProbe.satisfied;
        },
        {
          timeout: pollDeadlineMs,
          intervals: [2_000, 3_000, 5_000],
          message: () => formatDiagnostics(lastProbe, sessionId)
        }
      )
      .toBe(true);
  } catch (error) {
    const diagnostics = formatDiagnostics(lastProbe, sessionId);
    throw new Error(`${error instanceof Error ? error.message : String(error)}\n${diagnostics}`);
  }

  const resultText = lastProbe.agentRuns[0]?.resultText ?? "";
  expect(resultText).not.toBe("Hello from synthetic.");
  expect(resultText.toLowerCase()).toContain("oven");

  await page.getByRole("button", { name: "Background work" }).click();
  const drawer = page.getByRole("dialog", { name: "Background work" });
  await drawer.getByRole("button", { name: "View history", exact: true }).first().click();
  await expect(page.getByRole("dialog").getByText(resultText).first()).toBeVisible({ timeout: 30_000 });

  const transcriptAfter = await page.locator(".conversation-scroll").innerText();
  const helloAfter = (transcriptAfter.match(/Hello from synthetic/g) ?? []).length;
  expect(helloAfter).toBe(helloBefore);

  await page.reload();
  await expect(page.getByText("This conversation has ended.")).toBeVisible({ timeout: 15_000 });
  await page.getByRole("button", { name: "Background work" }).click();
  await drawer.getByRole("button", { name: "View history", exact: true }).first().click();
  await expect(page.getByRole("dialog").getByText(resultText).first()).toBeVisible({ timeout: 15_000 });
  await expect(page.locator(".conversation-scroll")).not.toContainText(resultText);
});
