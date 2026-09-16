export type AgentDescriptor = {
  id: string;
  version: number;
  name: string;
  role: string;
  description: string;
  voiceAvailable: boolean;
};

export type HealthResponse = {
  status: string;
  profile: string;
  protocolVersion: number;
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

export async function getHealth(): Promise<HealthResponse> {
  const response = await fetch("/health");
  if (!response.ok) {
    throw new Error("Unable to read health.");
  }

  return (await response.json()) as HealthResponse;
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
  const response = await fetch(`/api/v1/sessions/${sessionId}`, { method: "DELETE" });
  if (!response.ok) {
    throw new Error("Unable to end the session.");
  }
}
