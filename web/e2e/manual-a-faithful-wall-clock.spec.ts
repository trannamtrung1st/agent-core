import { execFileSync } from "node:child_process";
import { expect, test } from "@playwright/test";

const dbPath = process.env.PLAYWRIGHT_SQLITE_PATH ?? "";

function sqlite(script: string, args: string[] = []): string {
  return execFileSync("python3", ["-c", script, dbPath, ...args], { encoding: "utf8" }).trim();
}

test("MANUAL_A faithful wall-clock detached reminder", async ({ page }) => {
  test.setTimeout(180_000);

  await page.goto("/");
  await page.waitForFunction(() => window.localStorage.getItem("agent-core.owner-capability"));
  await page.getByRole("button", { name: "Start a new chat" }).click();
  await expect(page.getByRole("combobox", { name: "Identity" })).toBeEnabled({ timeout: 15_000 });
  await page.getByRole("combobox", { name: "Identity" }).click();
  await page.locator(".ant-select-item-option", { hasText: "Riley — General assistant" }).click();
  await expect(page.getByTestId("connection")).toHaveText("Ready", { timeout: 15_000 });

  await page.getByLabel("Message").fill("check the oven in 1 minute for me");
  await page.getByRole("button", { name: "Send" }).click();
  const transcript = page.locator(".conversation-scroll");
  await expect(transcript.getByText(/Scheduled/i)).toBeVisible({ timeout: 20_000 });

  const sessionId = sqlite(`
import sqlite3, sys
con = sqlite3.connect(sys.argv[1], timeout=30)
con.execute("PRAGMA busy_timeout=8000")
row = con.execute("SELECT SessionId FROM Sessions ORDER BY UpdatedAtUtc DESC LIMIT 1").fetchone()
con.close()
if row is None or not row[0]:
    raise SystemExit("no session row was stored")
print(row[0])
`);

  await page.getByRole("button", { name: "Conversation actions" }).click();
  await page.getByRole("menuitem", { name: "End" }).click();
  await expect(page.getByText("This conversation has ended.")).toBeVisible({ timeout: 15_000 });
  const transcriptBefore = await page.locator(".conversation-scroll").innerText();
  const helloBefore = (transcriptBefore.match(/Hello from synthetic/g) ?? []).length;

  await page.waitForTimeout(75_000);

  const counts = sqlite(
    `
import sqlite3, sys
db, session_id = sys.argv[1], sys.argv[2]
con = sqlite3.connect(db, timeout=30)
con.execute("PRAGMA busy_timeout=8000")
occ = con.execute(
    """SELECT COUNT(*) FROM TriggerOccurrences o
       JOIN TriggerRegistrations r ON r.RegistrationId = o.RegistrationId
       WHERE r.SourceSessionId=? AND o.Disposition=6""",
    (session_id,),
).fetchone()[0]
work = con.execute(
    "SELECT COUNT(*) FROM WorkItems WHERE SourceSessionId=?",
    (session_id,),
).fetchone()[0]
con.close()
print(f"{occ},{work}")
`,
    [sessionId]
  );
  const [occurrenceCount, workItemCount] = counts.split(",").map((part) => Number(part));
  expect(occurrenceCount).toBe(1);
  expect(workItemCount).toBe(1);

  await page.getByRole("button", { name: "Background work" }).click();
  const drawer = page.getByRole("dialog", { name: "Background work" });
  const resultText = sqlite(
    `
import sqlite3, sys
db, session_id = sys.argv[1], sys.argv[2]
con = sqlite3.connect(db, timeout=30)
con.execute("PRAGMA busy_timeout=8000")
row = con.execute(
    "SELECT ResultText FROM WorkItems WHERE SourceSessionId=? ORDER BY UpdatedAtUtc DESC LIMIT 1",
    (session_id,),
).fetchone()
con.close()
print(row[0] if row and row[0] else "")
`,
    [sessionId]
  );
  expect(resultText).not.toBe("Hello from synthetic.");
  expect(resultText.toLowerCase()).toContain("oven");

  await expect(drawer.getByText(resultText).first()).toBeVisible({ timeout: 30_000 });

  const transcriptAfter = await page.locator(".conversation-scroll").innerText();
  const helloAfter = (transcriptAfter.match(/Hello from synthetic/g) ?? []).length;
  expect(helloAfter).toBe(helloBefore);

  await page.reload();
  await expect(page.getByText("This conversation has ended.")).toBeVisible({ timeout: 15_000 });
  await page.getByRole("button", { name: "Background work" }).click();
  await expect(drawer.getByText(resultText).first()).toBeVisible({ timeout: 15_000 });
  await expect(page.locator(".conversation-scroll")).not.toContainText(resultText);
});
