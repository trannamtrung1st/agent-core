const SESSION_ID_PATTERN =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

const SESSION_PATH_PATTERN =
  /^\/c\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\/?$/i;

export function isSessionId(value: string): boolean {
  return SESSION_ID_PATTERN.test(value);
}

export function normalizeSessionId(sessionId: string): string {
  return sessionId.toLowerCase();
}

export function sameSessionId(left: string | null | undefined, right: string | null | undefined): boolean {
  if (!left || !right) {
    return false;
  }

  return normalizeSessionId(left) === normalizeSessionId(right);
}

export function parseSessionIdFromPath(pathname: string): string | null {
  const match = SESSION_PATH_PATTERN.exec(pathname);
  if (!match) {
    return null;
  }

  const sessionId = match[1];
  return isSessionId(sessionId) ? normalizeSessionId(sessionId) : null;
}

export function sessionPath(sessionId: string): string {
  return `/c/${normalizeSessionId(sessionId)}`;
}

export function syncBrowserSessionPath(sessionId: string | null, mode: "push" | "replace" = "replace"): void {
  const next = sessionId ? sessionPath(sessionId) : "/";
  const current = window.location.pathname;
  if (current === next) {
    return;
  }

  const state = sessionId ? { sessionId } : null;
  if (mode === "push") {
    window.history.pushState(state, "", next);
  } else {
    window.history.replaceState(state, "", next);
  }
}
