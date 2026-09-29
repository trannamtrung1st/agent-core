import { OwnerCapabilityError } from "../../services/api";

export interface AdminDeletionBlockedMessage {
  headline: string;
  bullets: string[];
  footer: string;
}

/** Parses API refusal text from `AdminDeletionMessages` in the backend. */
export function parseAdminDeletionBlockedMessage(message: string): AdminDeletionBlockedMessage | null {
  const lines = message.split("\n").map((line) => line.trimEnd());
  const headline = lines[0]?.trim() ?? "";
  if (!headline.startsWith("Cannot delete this ")) {
    return null;
  }

  const refIndex = lines.findIndex((line) => line.trim() === "It is still referenced by:");
  if (refIndex < 0) {
    return null;
  }

  const bullets: string[] = [];
  let index = refIndex + 1;
  while (index < lines.length) {
    const line = lines[index].trim();
    if (line === "") {
      index += 1;
      break;
    }
    if (line.startsWith("• ")) {
      bullets.push(line.slice(2).trim());
      index += 1;
      continue;
    }
    break;
  }

  const footer = lines
    .slice(index)
    .map((line) => line.trim())
    .filter((line) => line.length > 0)
    .join(" ");

  if (bullets.length === 0) {
    return null;
  }

  return { headline, bullets, footer };
}

export function formatAdminLoadError(error: unknown): { message: string; unauthorized: boolean } {
  if (error instanceof OwnerCapabilityError) {
    return {
      message: "Owner capability is missing or invalid. Refresh the page or reopen Chat to re-authorize.",
      unauthorized: true
    };
  }

  return {
    message: error instanceof Error ? error.message : "Request failed.",
    unauthorized: false
  };
}
