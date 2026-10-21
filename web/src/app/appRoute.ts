import { parseAccessRoute, type AccessRoute } from '../features/access/accessRoute';
import { newChatPath, parseTenantChatRoute, tenantIdFromPath } from './sessionRoute';
import { definitionPath, draftPath, globalPath, instancePages, instancePath, type AdminCollection, type DefinitionPage, type InstancePage } from '../features/admin/adminNavigation';
export type { AdminCollection };
export type AdminDefinitionTab = DefinitionPage;
// Input compatibility for callers being migrated; parsed routes contain only canonical pages.
export type AdminInstanceTab = 'identity' | 'skills' | 'continuity' | 'automation' | 'activity' | 'credentials' | 'effective';
export type AdminInstanceSection = 'profile' | 'settings' | 'workspace' | 'skills' | 'resources' | 'sessions' | 'runs' | 'memory' | 'experience' | 'automations' | 'controls';
export type AppArea = AppRoute['area'];
export type AppRoute = { area: 'chat'; tenantId: string } | AccessRoute;
export function parseAppRoute(pathname: string): AppRoute {
  const chat = parseTenantChatRoute(pathname);
  if (chat) return { area: 'chat', tenantId: chat.tenantId };
  if (pathname === '/') return { area: 'access', view: 'account', page: 'organizations' };
  return parseAccessRoute(pathname) ?? { area: 'access', view: 'not-found' };
}
// These builders remain only for shared controls awaiting explicit tenant transports.
const legacyInstance: Record<string, InstancePage> = {
  '': 'overview', identity: 'profile', 'identity/profile': 'profile', 'identity/settings': 'instructions', behavior: 'instructions', access: 'capabilities',
  'identity/workspace': 'workspace', 'skills/skills': 'skills', 'skills/resources': 'resources',
  continuity: 'memory', automation: 'automations', 'automation/automations': 'automations',
  'automation/controls': 'automations', 'automation/harness': 'self-management', activity: 'sessions', runs: 'runs',
};
export const adminHomePath = globalPath;
export const adminDefinitionPath = definitionPath;
export const adminDraftPath = draftPath;
export function adminInstancePath(id: string, page?: InstancePage | AdminInstanceTab, section?: AdminInstanceSection): string {
  const leaf = `${page ?? ''}${section ? `/${section}` : ''}`;
  const canonical = (!section ? instancePages.find(p => p.id === page)?.id : undefined) ?? legacyInstance[leaf];
  return instancePath(id, canonical ?? (page === 'activity' ? section === 'runs' ? 'runs' : 'sessions' : page === 'continuity' ? section === 'experience' ? 'experience' : 'memory' : 'overview'));
}
/** The root enters Account organization selection. Retired URLs never infer tenant authority. */
export function canonicalAppUrl(path: string): string {
  const url = new URL(path, window.location.origin);
  return url.origin === window.location.origin && url.pathname === '/' ? '/account/organizations' : path;
}
const LAST_CHAT_URL_KEY = 'agent-core:last-chat-url';
export function rememberChatUrl(path: string): void {
  const url = new URL(path, window.location.origin);
  const tenant = parseTenantChatRoute(url.pathname);
  if (url.origin === window.location.origin && tenant) {
    sessionStorage.setItem(`${LAST_CHAT_URL_KEY}:${tenant.tenantId}`, url.pathname + url.search + url.hash);
  }
}
export function lastChatUrl(): string {
  const tenant = tenantIdFromPath(window.location.pathname);
  if (!tenant) return '/account/organizations';
  return sessionStorage.getItem(`${LAST_CHAT_URL_KEY}:${tenant}`) ?? newChatPath();
}
export type NavigationGuard = (path: string) => boolean | Promise<boolean>;
let guard: NavigationGuard | undefined;
export function registerNavigationGuard(next: NavigationGuard): () => void { guard = next; return () => { if (guard === next) guard = undefined; }; }
export function approveAppNavigation(path: string): Promise<boolean> { return Promise.resolve(guard?.(path) ?? true); }
export function initializeAppHistory() {
  if (typeof window.history.state?.acIndex !== 'number') window.history.replaceState({ ...window.history.state, acIndex: 0 }, '', window.location.href);
}
export function navigateToAppPath(path: string, replace = false): void {
  const commit = () => {
    const currentChat = parseTenantChatRoute(window.location.pathname); const nextChat = parseTenantChatRoute(new URL(path, window.location.origin).pathname);
    if ((currentChat || nextChat) && (!currentChat || !nextChat || currentChat.tenantId !== nextChat.tenantId)) {
      window.location[replace ? 'replace' : 'assign'](path); return;
    }
    initializeAppHistory();
    const index = window.history.state.acIndex as number;
    window.history[replace ? 'replaceState' : 'pushState']({ acIndex: replace ? index : index + 1 }, '', canonicalAppUrl(path));
    window.dispatchEvent(Object.assign(new PopStateEvent('popstate', { state: window.history.state }), { appNavigation: true }));
  };
  const approved = guard?.(path) ?? true;
  if (typeof approved === "boolean") { if (approved) commit(); } else void approved.then(ok => { if (ok) commit(); });
}
