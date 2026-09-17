import { ConfigProvider } from "antd";
import { AppShell } from "./app/AppShell";
import { antdTheme } from "./app/antdTheme";
import { ChatApp } from "./features/chat/ChatApp";

export function App() {
  return (
    <ConfigProvider theme={antdTheme}>
      <AppShell>
        <ChatApp />
      </AppShell>
    </ConfigProvider>
  );
}
