export type AppArea = "chat" | "admin";

export type AdminRoute =
  | { area: "admin"; view: "home" }
  | { area: "admin"; view: "definition"; definitionId: string }
  | { area: "admin"; view: "instance"; instanceId: string };

export type AppRoute = { area: "chat" } | AdminRoute;

const ADMIN_HOME = "/admin";
const ADMIN_DEFINITION_PATTERN = /^\/admin\/definitions\/([^/]+)\/?$/i;
const ADMIN_INSTANCE_PATTERN = /^\/admin\/instances\/([0-9a-f-]{36})\/?$/i;

export function parseAppRoute(pathname: string): AppRoute {
  if (pathname === ADMIN_HOME || pathname === `${ADMIN_HOME}/`) {
    return { area: "admin", view: "home" };
  }

  const definitionMatch = ADMIN_DEFINITION_PATTERN.exec(pathname);
  if (definitionMatch) {
    return { area: "admin", view: "definition", definitionId: decodeURIComponent(definitionMatch[1]) };
  }

  const instanceMatch = ADMIN_INSTANCE_PATTERN.exec(pathname);
  if (instanceMatch) {
    return { area: "admin", view: "instance", instanceId: instanceMatch[1].toLowerCase() };
  }

  return { area: "chat" };
}

export function adminHomePath(): string {
  return ADMIN_HOME;
}

export function adminDefinitionPath(definitionId: string): string {
  return `/admin/definitions/${encodeURIComponent(definitionId)}`;
}

export function adminInstancePath(instanceId: string): string {
  return `/admin/instances/${instanceId.toLowerCase()}`;
}

const LAST_CHAT_URL_KEY = "agent-core:last-chat-url";

export function rememberChatUrl(pathname: string): void {
  if (pathname === "/" || pathname.startsWith("/c/")) {
    sessionStorage.setItem(LAST_CHAT_URL_KEY, pathname);
  }
}

export function lastChatUrl(): string {
  return sessionStorage.getItem(LAST_CHAT_URL_KEY) ?? "/";
}

export function navigateToAppPath(path: string, replace = false): void {
  if (replace) {
    window.history.replaceState(null, "", path);
  } else {
    window.history.pushState(null, "", path);
  }
  window.dispatchEvent(new PopStateEvent("popstate"));
}
