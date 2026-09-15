export type AgentDescriptor = {
  id: string;
  version: number;
  name: string;
  role: string;
  description: string;
  voiceAvailable: boolean;
};

export type SessionResponse = {
  sessionId: string;
  agentId: string;
  agentVersion: number;
  mode: string;
  pendingMode: string | null;
  status: string;
};

export async function listAgents(): Promise<AgentDescriptor[]> {
  const response = await fetch("/api/v1/agents");
  if (!response.ok) {
    throw new Error("Unable to list agents.");
  }

  const body = (await response.json()) as { agents: AgentDescriptor[] };
  return body.agents;
}

export async function createSession(agentId: string, mode = "text"): Promise<SessionResponse> {
  const response = await fetch("/api/v1/sessions", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ agentId, mode })
  });
  if (!response.ok) {
    throw new Error("Unable to create a session.");
  }

  return (await response.json()) as SessionResponse;
}

export async function endSession(sessionId: string): Promise<void> {
  await fetch(`/api/v1/sessions/${sessionId}`, { method: "DELETE" });
}
