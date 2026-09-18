import { Flex, Select, Typography } from "antd";
import type { AgentDescriptor } from "../../services/api";
import { SessionFailureAlert } from "./SessionFailureAlert";
import type { SessionErrorView } from "./sessionError";
import { SpeechLocalePicker } from "./SpeechLocalePicker";

export function AgentPicker({
  agents,
  selectedAgentId,
  error,
  speechLocale,
  onSelect,
  onSpeechLocaleChange
}: {
  agents: AgentDescriptor[];
  selectedAgentId: string;
  error: SessionErrorView | string | null;
  speechLocale: string;
  onSelect: (agentId: string) => void;
  onSpeechLocaleChange: (locale: string | null) => void;
}) {
  const hasAgents = agents.length > 0;

  return (
    <Flex vertical gap={8} className="agent-picker">
      {error ? <SessionFailureAlert error={error} /> : null}
      <Typography.Text>Identity</Typography.Text>
      <Select
        aria-label="Identity"
        value={hasAgents ? selectedAgentId : undefined}
        placeholder="Select agent"
        disabled={!hasAgents}
        options={agents.map((agent) => ({
          value: agent.id,
          label: `${agent.name} — ${agent.role}`
        }))}
        onChange={(agentId) => onSelect(agentId)}
        style={{ width: "100%" }}
      />
      <SpeechLocalePicker value={speechLocale} onChange={onSpeechLocaleChange} />
    </Flex>
  );
}
