import { AccessApp } from "../features/access/AccessApp";
import { useEffect, useRef, useState } from "react";
import { TenantChatEntry } from '../features/chat/TenantChatEntry';
import { navigateFromBrowserHistory, suspendLiveSessionForNavigation } from "../services/realtime";
import { approveAppNavigation, canonicalAppUrl, initializeAppHistory, parseAppRoute, rememberChatUrl, type AppRoute } from "./appRoute";

export function AppRouter() {
  initializeAppHistory();
  const canonical = canonicalAppUrl(window.location.pathname + window.location.search + window.location.hash);
  if (canonical !== window.location.pathname + window.location.search + window.location.hash) window.history.replaceState(window.history.state, '', canonical);
  const [route, setRoute] = useState<AppRoute>(() => parseAppRoute(window.location.pathname));

  const currentRoute = useRef(route);

  useEffect(() => {
    rememberChatUrl(window.location.pathname + window.location.search + window.location.hash);
  }, []);

  useEffect(() => {
    let acceptedIndex = window.history.state.acIndex as number;
    let acceptedUrl = window.location.pathname + window.location.search + window.location.hash;
    let restoring: (() => void) | null = null;
    let approvedUrl: string | null = null;
    const apply = () => {
      acceptedIndex = window.history.state?.acIndex ?? acceptedIndex;
      acceptedUrl = window.location.pathname + window.location.search + window.location.hash;
      const next = parseAppRoute(window.location.pathname);
      const previousArea = currentRoute.current.area;
      currentRoute.current = next;
      setRoute(next);
      if (next.area !== "chat") {
        void suspendLiveSessionForNavigation();
        return;
      }

      // Mounting ChatApp bootstraps its deep link. Avoid a second concurrent attach/reopen.
      if (previousArea === "chat") void navigateFromBrowserHistory();
    };
    const onPopState = (event: PopStateEvent) => {
      if (restoring) { const restored = restoring; restoring = null; restored(); return; }
      const nextUrl = window.location.pathname + window.location.search + window.location.hash;
      const nextIndex = window.history.state?.acIndex as number | undefined;
      if ((event as PopStateEvent & { appNavigation?: boolean }).appNavigation || nextUrl === approvedUrl) {
        approvedUrl = null; apply(); return;
      }
      if (nextIndex !== undefined && nextIndex !== acceptedIndex) {
        const delta = nextIndex - acceptedIndex;
        // Native traversal restores the accepted entry while the owner guard decides.
        restoring = () => { void approveAppNavigation(nextUrl).then(ok => {
          if (ok) { approvedUrl = nextUrl; window.history.go(delta); }
        }); };
        window.history.go(-delta);
      } else if (nextUrl !== acceptedUrl) apply();
    };
    window.addEventListener("popstate", onPopState);
    return () => window.removeEventListener("popstate", onPopState);
  }, []);

  useEffect(() => {
    if (route.area !== "chat") {
      void suspendLiveSessionForNavigation();
    }
  }, [route.area]);

  if (route.area === "access") return <AccessApp key={route.view === 'tenant' ? route.tenantId : route.view} route={route} />;
  return <TenantChatEntry tenantId={route.tenantId} />;
}
