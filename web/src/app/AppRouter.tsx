import { useEffect, useState } from "react";
import { ChatApp } from "../features/chat/ChatApp";
import { AdminApp } from "../features/admin/AdminApp";
import { navigateFromBrowserHistory, suspendLiveSessionForNavigation } from "../services/realtime";
import { adminHomePath, navigateToAppPath, parseAppRoute, rememberChatUrl, type AppRoute } from "./appRoute";

export function AppRouter() {
  const [route, setRoute] = useState<AppRoute>(() => parseAppRoute(window.location.pathname));

  useEffect(() => {
    rememberChatUrl(window.location.pathname);
  }, []);

  useEffect(() => {
    const onPopState = () => {
      const next = parseAppRoute(window.location.pathname);
      setRoute(next);
      if (next.area === "admin") {
        void suspendLiveSessionForNavigation();
        return;
      }

      void navigateFromBrowserHistory();
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
