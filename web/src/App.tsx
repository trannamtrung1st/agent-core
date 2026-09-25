import { ConfigProvider } from "antd";
import { AppShell } from "./app/AppShell";
import { antdTheme } from "./app/antdTheme";
import { AppRouter } from "./app/AppRouter";

export function App() {
  return (
    <ConfigProvider theme={antdTheme}>
      <AppShell>
        <AppRouter />
      </AppShell>
    </ConfigProvider>
  );
}
