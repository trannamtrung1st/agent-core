import { OwnerCapabilityError } from "../../services/api";

export type AdminFailureNotice = {
  message: string;
  diagnosticId?: string;
};

export type AdminErrorReporter = (message: string | null, diagnosticId?: string | null) => void;

function adminRequestNotice(error: unknown): AdminFailureNotice | null {
  if (!(error instanceof Error) || error.name !== "AdminRequestError") {
    return null;
  }

  const diagnosticId = "diagnosticId" in error && typeof error.diagnosticId === "string" && error.diagnosticId.length > 0
    ? error.diagnosticId
    : undefined;
  const message = error.message;
  return diagnosticId ? { message, diagnosticId } : { message };
}

/** Keeps a server diagnostic id when the failure is an admin request error. */
export function describeAdminError(error: unknown, fallback: string): AdminFailureNotice {
  const adminRequest = adminRequestNotice(error);
  if (adminRequest) {
    return adminRequest.message ? adminRequest : { ...adminRequest, message: fallback };
  }

  return { message: error instanceof Error && error.message ? error.message : fallback };
}

export function reportAdminError(onError: AdminErrorReporter, error: unknown, fallback: string) {
  const notice = describeAdminError(error, fallback);
  onError(notice.message, notice.diagnosticId ?? null);
}

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

export function formatAdminLoadError(
  error: unknown
): AdminFailureNotice & { unauthorized: boolean } {
  if (error instanceof OwnerCapabilityError) {
    return {
      message: "Owner capability is missing or invalid. Refresh the page or reopen Chat to re-authorize.",
      unauthorized: true
    };
  }

  return { ...describeAdminError(error, "Request failed."), unauthorized: false };
}
