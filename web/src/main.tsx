import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { App as AntApp, ConfigProvider } from "antd";
import { App } from "./App";
import "./app.css";
import "./styles.css";

const root = document.getElementById("root");
if (!root) {
  throw new Error("Root element missing.");
}

createRoot(root).render(
  <StrictMode>
    <ConfigProvider>
      <AntApp className="antd-root">
        <App />
      </AntApp>
    </ConfigProvider>
  </StrictMode>
);
