import { parseAccessRoute, type AccessRoute } from '../features/access/accessRoute';
import { newChatPath, parseTenantChatRoute, tenantIdFromPath } from './sessionRoute';
export type AppArea = AppRoute['area'];
export type AppRoute = { area: 'chat'; tenantId: string } | AccessRoute;
export function parseAppRoute(pathname: string): AppRoute {
  const chat = parseTenantChatRoute(pathname);
  if (chat) return { area: 'chat', tenantId: chat.tenantId };
  if (pathname === '/') return { area: 'access', view: 'account', page: 'organizations' };
  return parseAccessRoute(pathname) ?? { area: 'access', view: 'not-found' };
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
const guards = new Map<symbol, NavigationGuard>();
export function registerNavigationGuard(next: NavigationGuard): () => void { const key = Symbol(); guards.set(key, next); return () => { guards.delete(key); }; }
function navigationApproval(path: string): boolean | Promise<boolean> {
  const active = [...guards.values()].reverse();
  function check(from: number): boolean | Promise<boolean> {
    for (let index = from; index < active.length; index++) {
      const result = active[index](path);
      if (typeof result !== 'boolean') return result.then(allowed => allowed ? check(index + 1) : false);
      if (!result) return false;
    }
    return true;
  }
  return check(0);
}
export function approveAppNavigation(path: string): Promise<boolean> { return Promise.resolve(navigationApproval(path)); }
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
  const approved = navigationApproval(path);
  if (typeof approved === "boolean") { if (approved) commit(); } else void approved.then(ok => { if (ok) commit(); });
}
