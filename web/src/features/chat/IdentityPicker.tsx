import { Select } from "../../components/Select";
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
  return (
    <section className="picker">
      {error ? (
        <p className="error" role="alert">
          {error}
        </p>
      ) : null}
      <div className="picker-field">
        <span className="picker-legend">Identity</span>
        <Select
          aria-label="Identity"
          value={selectedAgentId}
          options={agents.map((agent) => ({
            value: agent.id,
            label: `${agent.name} — ${agent.role}`
          }))}
          onChange={onSelect}
        />
      </div>
      <button type="button" aria-label="Start conversation" onClick={onStart}>
        Start conversation
      </button>
    </section>
  );
}
