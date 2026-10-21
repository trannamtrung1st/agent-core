import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { App } from "./App";
import "./app.css";

// Retired credentials have no role in Account/tenant authentication. Remove only
// the obsolete credential key; blocked browser storage must not block sign-in.
try { window.localStorage.removeItem("agent-core.owner-capability"); } catch { /* Storage can be unavailable. */ }

const root = document.getElementById("root");
if (!root) {
  throw new Error("Root element missing.");
}

createRoot(root).render(
  <StrictMode>
    <App />
  </StrictMode>
);
