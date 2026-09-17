import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { ConfigProvider } from "antd";
import { App } from "./App";
import "./app.css";

const root = document.getElementById("root");
if (!root) {
  throw new Error("Root element missing.");
}

createRoot(root).render(
  <StrictMode>
    <ConfigProvider>
      <App />
    </ConfigProvider>
  </StrictMode>
);
