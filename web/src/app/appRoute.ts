import { definitionPages, definitionPath, draftPages, draftPath, globalPages, globalPath, instancePages, instancePath, legacyDraftPages, type AdminCollection, type DefinitionPage, type DraftPage, type InstancePage } from '../features/admin/adminNavigation';
export type { AdminCollection };
export type AdminDefinitionTab = DefinitionPage;
// Input compatibility for callers being migrated; parsed routes contain only canonical pages.
export type AdminInstanceTab = 'identity' | 'skills' | 'continuity' | 'automation' | 'activity' | 'credentials' | 'effective';
export type AdminInstanceSection = 'profile' | 'settings' | 'workspace' | 'skills' | 'resources' | 'sessions' | 'runs' | 'memory' | 'experience' | 'automations' | 'controls';
export type AppArea = 'chat' | 'admin';
export type AdminRoute =
  | { area: 'admin'; view: 'home'; collection?: AdminCollection }
  | { area: 'admin'; view: 'definition'; definitionId: string; tab?: DefinitionPage }
  | { area: 'admin'; view: 'draft'; definitionId: string; draftId: string; page: DraftPage }
  | { area: 'admin'; view: 'instance'; instanceId: string; page: InstancePage }
  | { area: 'admin'; view: 'not-found' };
export type AppRoute = { area: 'chat' } | AdminRoute;
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const missing: AdminRoute = { area: 'admin', view: 'not-found' };
const legacyInstance: Record<string, InstancePage> = {
  '': 'overview', identity: 'profile', 'identity/profile': 'profile', 'identity/settings': 'instructions', behavior: 'instructions', access: 'capabilities',
  'identity/workspace': 'workspace', 'skills/skills': 'skills', 'skills/resources': 'resources',
  continuity: 'memory', automation: 'automations', 'automation/automations': 'automations',
  'automation/controls': 'automations', 'automation/harness': 'self-management', activity: 'sessions',
  runs: 'runs',
};
export function parseAppRoute(pathname: string): AppRoute {
  if (!/^\/admin(?:\/|$)/i.test(pathname)) return { area: 'chat' };
  const path = pathname.replace(/\/$/, '');
  if (path.toLowerCase() === '/admin') return { area: 'admin', view: 'home', collection: 'instances' };
  const global = globalPages.find(p => `/admin/${p.path}` === path.toLowerCase());
  if (global) return { area: 'admin', view: 'home', collection: global.id };
  if (path.toLowerCase() === '/admin/connections') return { area: 'admin', view: 'home', collection: 'credentials' };
  const instance = /^\/admin\/instances\/([^/]+)(?:\/(.*))?$/i.exec(path);
  if (instance) {
    if (!uuid.test(instance[1])) return missing;
    const leaf = (instance[2] ?? '').toLowerCase();
    const page = instancePages.find(p => p.path === leaf)?.id ?? legacyInstance[leaf];
    return page ? { area: 'admin', view: 'instance', instanceId: instance[1].toLowerCase(), page } : missing;
  }
  const definition = /^\/admin\/definitions\/([^/]+)(?:\/(.*))?$/i.exec(path);
  if (definition) {
    let id: string;
    try { id = decodeURIComponent(definition[1]); } catch { return missing; }
    if (!id || /[\/\\\u0000-\u001f]/.test(id)) return missing;
    const leaf = definition[2] ?? '';
    if (!leaf || definitionPages.some(p => p.path === leaf)) return { area: 'admin', view: 'definition', definitionId: id, tab: leaf === 'drafts' ? 'drafts' : 'versions' };
    const draft = /^drafts\/([^/]+)(?:\/([^/]+))?$/.exec(leaf);
    if (draft && uuid.test(draft[1])) {
      const page = draft[2] ?? 'overview';
      const canonical = draftPages.find(p => p.path === page)?.id ?? legacyDraftPages[page];
      if (canonical) return { area: 'admin', view: 'draft', definitionId: id, draftId: draft[1].toLowerCase(), page: canonical };
    }
  }
  return missing;
}
export const adminHomePath = globalPath;
export const adminDefinitionPath = definitionPath;
export const adminDraftPath = draftPath;
export function adminInstancePath(id: string, page?: InstancePage | AdminInstanceTab, section?: AdminInstanceSection): string {
  const leaf = `${page ?? ''}${section ? `/${section}` : ''}`;
  const canonical = (!section ? instancePages.find(p => p.id === page)?.id : undefined) ?? legacyInstance[leaf];
  return instancePath(id, canonical ?? (page === 'activity' ? section === 'runs' ? 'runs' : 'sessions' : page === 'continuity' ? section === 'experience' ? 'experience' : 'memory' : 'overview'));
}
export function adminRoutePath(route: AdminRoute): string | null {
  if (route.view === 'home') return globalPath(route.collection);
  if (route.view === 'instance') return instancePath(route.instanceId, route.page);
  if (route.view === 'definition') return definitionPath(route.definitionId, route.tab);
  if (route.view === 'draft') return draftPath(route.definitionId, route.draftId, route.page);
  return null;
}
/** Only meaningful presentation queries cross a legacy redirect. Never forward arbitrary credentials. */
export function canonicalAppUrl(path: string): string {
  const url = new URL(path, window.location.origin);
  let route = parseAppRoute(url.pathname);
  const settingPage: Record<string, InstancePage> = { instructions: 'instructions', conversationPolicy: 'conversation', behaviorPolicy: 'behavior', initiativePolicy: 'initiative', voice: 'voice', modelDefaults: 'models', providerPreferences: 'providers', sandboxPolicy: 'sandbox', 'execution-budgets': 'execution', capabilities: 'capabilities', credentials: 'credentials', memoryPolicy: 'memory-policy', restrictions: 'trigger-policy', triggerPolicy: 'trigger-policy', 'execution-defaults': 'unattended', registrations: 'automations' };
  const anchor = url.hash.slice(1);
  if (route.area === 'admin' && route.view === 'instance') {
    const page = /\/automation\/controls\/?$/.test(url.pathname) ? 'unattended' : settingPage[anchor];
    if (page) route = { ...route, page };
  }
  if (route.area === 'admin' && route.view === 'draft') {
    const page = anchor === 'metadata' ? 'metadata' : anchor === 'selfManagement' ? 'self-management' : settingPage[anchor];
    if (page && draftPages.some(p => p.id === page)) route = { ...route, page: page as DraftPage };
  }
  if (route.area !== 'admin') return path;
  const canonical = adminRoutePath(route);
  if (!canonical || canonical === url.pathname) return path;
  const safe = new URLSearchParams();
  for (const key of ['session', 'run', 'automation', 'emailAccount', 'driveAccount', 'event', 'builtin', 'returnTo', 'q']) {
    const value = url.searchParams.get(key); if (value !== null) safe.set(key, value);
  }
  let hash = url.hash;
  if (/\/automation\/controls\/?$/.test(url.pathname)) hash = '#execution-defaults';
  return `${canonical}${safe.size ? `?${safe}` : ''}${hash}`;
}
const LAST_CHAT_URL_KEY = 'agent-core:last-chat-url';
export function rememberChatUrl(path: string): void { const url = new URL(path, window.location.origin); if (url.origin === window.location.origin && (url.pathname === '/' || url.pathname.startsWith('/c/'))) sessionStorage.setItem(LAST_CHAT_URL_KEY, url.pathname + url.search + url.hash); }
export function lastChatUrl(): string { return sessionStorage.getItem(LAST_CHAT_URL_KEY) ?? '/'; }
export type NavigationGuard = (path: string) => boolean | Promise<boolean>;
let guard: NavigationGuard | undefined;
export function registerNavigationGuard(next: NavigationGuard): () => void { guard = next; return () => { if (guard === next) guard = undefined; }; }
export function approveAppNavigation(path: string): Promise<boolean> { return Promise.resolve(guard?.(path) ?? true); }
export function initializeAppHistory() {
  if (typeof window.history.state?.acIndex !== 'number') window.history.replaceState({ ...window.history.state, acIndex: 0 }, '', window.location.href);
}
export function navigateToAppPath(path: string, replace = false): void {
  const commit = () => {
    initializeAppHistory();
    const index = window.history.state.acIndex as number;
    window.history[replace ? 'replaceState' : 'pushState']({ acIndex: replace ? index : index + 1 }, '', canonicalAppUrl(path));
    window.dispatchEvent(Object.assign(new PopStateEvent('popstate', { state: window.history.state }), { appNavigation: true }));
  };
  const approved = guard?.(path) ?? true;
  if (typeof approved === "boolean") { if (approved) commit(); } else void approved.then(ok => { if (ok) commit(); });
}
