export const USER_INTERRUPT_REASONS = new Set(["userSteer", "userStop", "userBargeIn"]);

export function interruptReasonLabel(reason: string | null | undefined): string | null {
  if (!reason) {
    return null;
  }

  switch (reason) {
    case "userSteer":
    case "userStop":
    case "userBargeIn":
      return import.meta.env.DEV ? `Interrupted — ${reason}` : "Interrupted";
    case "disconnected":
      return "Disconnected";
    case "modeChange":
      return "Mode changed";
    case "deactivated":
      return "Paused";
    case "ended":
      return "Session ended";
    case "newText":
      return import.meta.env.DEV ? "Interrupted — newText" : "Interrupted";
    default:
      return import.meta.env.DEV ? `Interrupted — ${reason}` : "Disconnected";
  }
}
