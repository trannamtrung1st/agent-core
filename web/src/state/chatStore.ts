import { create } from "zustand";
import type { AgentDescriptor } from "../services/api";
import { emptySession, type SessionView } from "./sessionStore";

export type ChatSnapshot = SessionView & {
  agents: AgentDescriptor[];
  selectedAgentId: string;
};

export const useChatStore = create<ChatSnapshot>(() => ({
  ...emptySession(),
  agents: [],
  selectedAgentId: "examiner"
}));
