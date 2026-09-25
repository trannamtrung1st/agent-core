import { useEffect, useState } from "react";
import { ChatApp } from "../features/chat/ChatApp";
import { AdminApp } from "../features/admin/AdminApp";
import { navigateFromBrowserHistory } from "../services/realtime";
import { parseAppRoute, rememberChatUrl, type AppRoute } from "./appRoute";

export function AppRouter() {
  const [route, setRoute] = useState<AppRoute>(() => parseAppRoute(window.location.pathname));

  useEffect(() => {
    rememberChatUrl(window.location.pathname);
  }, []);

  useEffect(() => {
    const onPopState = () => {
      const next = parseAppRoute(window.location.pathname);
      setRoute(next);
      if (next.area === "chat") {
        void navigateFromBrowserHistory();
      }
    };
    window.addEventListener("popstate", onPopState);
    return () => window.removeEventListener("popstate", onPopState);
  }, []);

  if (route.area === "admin") {
    return <AdminApp route={route} />;
  }

  return <ChatApp onOpenAdmin={() => {
    rememberChatUrl(window.location.pathname);
    window.history.pushState(null, "", "/admin");
    window.dispatchEvent(new PopStateEvent("popstate"));
  }} />;
}
