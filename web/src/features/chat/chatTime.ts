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

export function statusLabel(status: string, finishReason?: string | null): string | null {
  if (status === "completed" && finishReason === "lengthLimit") {
    return "Output limit reached";
  }
  if (status === "interrupted") {
    return "Interrupted";
  }
  if (status === "failed") {
    return "Failed";
  }
  return null;
}
