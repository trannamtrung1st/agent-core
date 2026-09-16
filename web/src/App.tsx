import { AppShell } from "./app/AppShell";
import { ChatApp } from "./features/chat/ChatApp";

export function App() {
  return (
    <AppShell>
      <ChatApp />
    </AppShell>
  );
}
