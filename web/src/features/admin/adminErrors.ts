import { OwnerCapabilityError } from "../../services/api";

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
