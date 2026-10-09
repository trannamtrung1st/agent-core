import { useEffect, useRef, useState } from "react";
import { ChatApp } from "../features/chat/ChatApp";
import { AdminApp } from "../features/admin/AdminApp";
import { navigateFromBrowserHistory, suspendLiveSessionForNavigation } from "../services/realtime";
import { adminHomePath, navigateToAppPath, parseAppRoute, rememberChatUrl, type AppRoute } from "./appRoute";

export function AppRouter() {
  const [route, setRoute] = useState<AppRoute>(() => parseAppRoute(window.location.pathname));

  const currentRoute = useRef(route);

  useEffect(() => {
    rememberChatUrl(window.location.pathname);
  }, []);

  useEffect(() => {
    const onPopState = () => {
      const next = parseAppRoute(window.location.pathname);
      const previousArea = currentRoute.current.area;
      currentRoute.current = next;
      setRoute(next);
      if (next.area === "admin") {
        void suspendLiveSessionForNavigation();
        return;
      }

      // Mounting ChatApp bootstraps its deep link. Avoid a second concurrent attach/reopen.
      if (previousArea === "chat") void navigateFromBrowserHistory();
    };
    window.addEventListener("popstate", onPopState);
    return () => window.removeEventListener("popstate", onPopState);
  }, []);

  useEffect(() => {
    if (route.area === "admin") {
      void suspendLiveSessionForNavigation();
    }
  }, [route.area]);

  if (route.area === "admin") {
    return <AdminApp route={route} />;
  }

  return (
    <ChatApp
      returnToActivity={(() => {
        const path = new URLSearchParams(window.location.search).get("returnTo");
        const target = path ? parseAppRoute(path.split("?")[0]) : null;
        return path && target?.area === "admin" && target.view === "instance" && target.tab === "activity" ? () => navigateToAppPath(path) : undefined;
      })()}
      onOpenAdmin={() => {
        void (async () => {
          rememberChatUrl(window.location.pathname);
          await suspendLiveSessionForNavigation();
          navigateToAppPath(adminHomePath());
        })();
      }}
    />
  );
}
