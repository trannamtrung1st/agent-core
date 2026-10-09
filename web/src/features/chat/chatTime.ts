import type { HistoryEntry } from "../../state/sessionStore";

export function messageTimestamp(entry: HistoryEntry, entries: readonly HistoryEntry[] = []): string {
  if (entry.role !== "assistant" || !["completed", "failed", "interrupted"].includes(entry.status)) return entry.createdAt;
  if (entry.completedAt) return entry.completedAt;

  // Older replies have no recorded completion time. Their progress notices provide
  // a durable lower bound, so the final reply must not display an earlier time.
  return entries.reduce((latest, notice) =>
    entry.responseId && notice.responseId === entry.responseId && notice.role === "applicationMessage"
      && Date.parse(notice.createdAt) > Date.parse(latest) ? notice.createdAt : latest,
  entry.createdAt);
}

export function formatChatTime(
  iso: string,
  now: Date = new Date(),
  locale?: string
): string | null {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) {
    return null;
  }

  const sameDay =
    date.getFullYear() === now.getFullYear() &&
    date.getMonth() === now.getMonth() &&
    date.getDate() === now.getDate();
  const sameYear = date.getFullYear() === now.getFullYear();

  return new Intl.DateTimeFormat(locale, {
    ...(sameDay
      ? { hour: "numeric", minute: "2-digit" }
      : sameYear
        ? { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" }
        : { month: "short", day: "numeric", year: "numeric", hour: "numeric", minute: "2-digit" })
  }).format(date);
}

import { interruptReasonLabel } from "./interruptReason";

export function statusLabel(
  status: string,
  finishReason?: string | null,
  interruptReason?: string | null
): string | null {
  if (status === "completed" && finishReason === "lengthLimit") {
    return "Output limit reached";
  }
  if (status === "interrupted") {
    return interruptReasonLabel(interruptReason) ?? "Interrupted";
  }
  if (status === "failed") {
    return "Failed";
  }
  return null;
}
