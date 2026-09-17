export function requiresExplicitResume(pauseReason?: string | null): boolean {
  return pauseReason !== "disconnected" && pauseReason !== "recovered";
}
