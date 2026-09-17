import { Alert, Button, Select } from "antd";
import type { AgentDescriptor } from "../../services/api";

export function IdentityPicker({
  agents,
  selectedAgentId,
  error,
  onSelect,
  onStart
}: {
  agents: AgentDescriptor[];
  selectedAgentId: string;
  error: string | null;
  onSelect: (agentId: string) => void;
  onStart: () => void;
}) {
  const hasAgents = agents.length > 0;
  const canStart = hasAgents && selectedAgentId.trim().length > 0;

  return (
    <section className="picker">
      {error ? <Alert type="error" showIcon title={error} /> : null}
      <div className="picker-field">
        <span className="picker-legend">Identity</span>
        <Select
          aria-label="Identity"
          value={hasAgents ? selectedAgentId : undefined}
          placeholder="Select identity"
          disabled={!hasAgents}
          options={agents.map((agent) => ({
            value: agent.id,
            label: `${agent.name} — ${agent.role}`
          }))}
          onChange={(agentId) => onSelect(agentId)}
          style={{ width: "100%" }}
        />
      </div>
      <Button type="primary" aria-label="Start conversation" disabled={!canStart} onClick={onStart}>
        Start conversation
      </Button>
    </section>
  );
}
