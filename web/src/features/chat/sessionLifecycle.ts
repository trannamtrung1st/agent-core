export function lifecycleOutcomeLabel(lifecycleStatus?: string | null): string {
  switch (lifecycleStatus) {
    case "completed":
      return "Completed";
    case "expired":
      return "Expired";
    case "cancelled":
      return "Cancelled";
    default:
      return "Ended";
  }
}

export function terminalSessionNote(lifecycleStatus?: string | null): string {
  switch (lifecycleStatus) {
    case "completed":
      return "This conversation is completed.";
    case "expired":
      return "This conversation has expired.";
    case "cancelled":
      return "This conversation was cancelled.";
    default:
      return "This conversation has ended.";
  }
}
