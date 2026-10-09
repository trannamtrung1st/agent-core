export type AppArea = "chat" | "admin";
export type AdminCollection = "definitions" | "instances" | "events" | "credentials";

export const ADMIN_DEFINITION_TABS = ["versions", "drafts"] as const;
export type AdminDefinitionTab = (typeof ADMIN_DEFINITION_TABS)[number];
export const ADMIN_INSTANCE_TABS = ["identity", "skills", "continuity", "automation", "activity", "credentials", "effective"] as const;
export type AdminInstanceTab = (typeof ADMIN_INSTANCE_TABS)[number];
export const ADMIN_CONTINUITY_TABS = ["memory", "experience"] as const;
export const ADMIN_AUTOMATION_TABS = ["automations", "controls"] as const;
export const ADMIN_IDENTITY_TABS = ["profile", "workspace"] as const;
export const ADMIN_ACTIVITY_TABS = ["sessions", "runs"] as const;
export type AdminInstanceSection = (typeof ADMIN_ACTIVITY_TABS)[number] | (typeof ADMIN_IDENTITY_TABS)[number] | (typeof ADMIN_CONTINUITY_TABS)[number] | (typeof ADMIN_AUTOMATION_TABS)[number];

export type AdminRoute =
  | { area: "admin"; view: "home"; collection?: AdminCollection }
  | { area: "admin"; view: "definition"; definitionId: string; tab?: AdminDefinitionTab }
  | { area: "admin"; view: "instance"; instanceId: string; tab?: AdminInstanceTab; section?: AdminInstanceSection };

export type AppRoute = { area: "chat" } | AdminRoute;

const ADMIN_HOME = "/admin";
const ADMIN_DEFINITION_PATTERN = /^\/admin\/definitions\/([^/]+)(?:\/([^/]+))?\/?$/i;
const ADMIN_INSTANCE_PATTERN = /^\/admin\/instances\/([0-9a-f-]{36})(?:\/([^/]+)(?:\/([^/]+))?)?\/?$/i;

export function parseAppRoute(pathname: string): AppRoute {
  if (pathname === ADMIN_HOME || pathname === `${ADMIN_HOME}/`) {
    return { area: "admin", view: "home" };
  }
  const collectionMatch = /^\/admin\/(definitions|instances)\/?$/i.exec(pathname);
  if (collectionMatch) {
    return { area: "admin", view: "home", collection: collectionMatch[1].toLowerCase() as AdminCollection };
  }

  const connectionMatch = /^\/admin\/connections(?:\/(credentials|events))?\/?$/i.exec(pathname);
  if (connectionMatch) return { area: "admin", view: "home", collection: connectionMatch[1] === "events" ? "events" : "credentials" };
  const definitionMatch = ADMIN_DEFINITION_PATTERN.exec(pathname);
  if (definitionMatch) {
    let definitionId: string;
    try { definitionId = decodeURIComponent(definitionMatch[1]); }
    catch { return { area: "chat" }; }
    const tab = definitionMatch[2]?.toLowerCase();
    return { area: "admin", view: "definition", definitionId,
      ...(ADMIN_DEFINITION_TABS.includes(tab as AdminDefinitionTab) ? { tab: tab as AdminDefinitionTab } : {}) };
  }

  const instanceMatch = ADMIN_INSTANCE_PATTERN.exec(pathname);
  if (instanceMatch) {
    const legacyRuns = instanceMatch[2]?.toLowerCase() === "runs";
    const tab = legacyRuns ? "activity" : instanceMatch[2]?.toLowerCase();
    const section = legacyRuns ? "runs" : instanceMatch[3]?.toLowerCase();
    return { area: "admin", view: "instance", instanceId: instanceMatch[1].toLowerCase(),
      ...(ADMIN_INSTANCE_TABS.includes(tab as AdminInstanceTab) ? { tab: tab as AdminInstanceTab } : {}),
      ...(isInstanceSection(tab, section) ? { section: section as AdminInstanceSection } : {}) };
  }

  return { area: "chat" };
}

export function adminHomePath(collection: AdminCollection = "definitions"): string {
  return collection === "definitions" ? ADMIN_HOME : collection === "instances" ? `${ADMIN_HOME}/instances` : `${ADMIN_HOME}/connections/${collection}`;
}

export function adminDefinitionPath(definitionId: string, tab?: AdminDefinitionTab): string {
  return `/admin/definitions/${encodeURIComponent(definitionId)}${tab ? `/${tab}` : ""}`;
}

function isInstanceSection(tab: string | undefined, section: string | undefined): boolean {
  return section !== undefined && (tab === "continuity"
    ? ADMIN_CONTINUITY_TABS.some(value => value === section)
    : tab === "activity" ? ADMIN_ACTIVITY_TABS.some(value => value === section)
    : tab === "identity" ? ADMIN_IDENTITY_TABS.some(value => value === section)
    : tab === "automation" && ADMIN_AUTOMATION_TABS.some(value => value === section));
}

export function adminInstancePath(instanceId: string, tab?: AdminInstanceTab, section?: AdminInstanceSection): string {
  return `/admin/instances/${instanceId.toLowerCase()}${tab ? `/${tab}` : ""}${isInstanceSection(tab, section) ? `/${section}` : ""}`;
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
