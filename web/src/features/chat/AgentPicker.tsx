import { Flex, Select, Typography } from "antd";
import type { AgentDescriptor } from "../../services/api";
import { SessionFailureAlert } from "./SessionFailureAlert";
import type { SessionErrorView } from "./sessionError";
import { SpeechLocalePicker } from "./SpeechLocalePicker";
import { ModelPicker, type ModelCatalogItem } from "./ModelPicker";

export function AgentPicker({
  agents,
  selectedAgentId,
  error,
  speechLocale,
  models,
  defaultModelKey,
  modelValue,
  effortValue,
  onSelect,
  onSpeechLocaleChange,
  onModelChange,
  onEffortChange
}: {
  agents: AgentDescriptor[];
  selectedAgentId: string;
  error: SessionErrorView | string | null;
  speechLocale: string;
  models: readonly ModelCatalogItem[];
  defaultModelKey: string | null;
  modelValue: string;
  effortValue: string | null;
  onSelect: (agentId: string) => void;
  onSpeechLocaleChange: (locale: string | null) => void;
  onModelChange: (key: string) => void;
  onEffortChange: (effort: string | null) => void;
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
      <ModelPicker
        models={models}
        defaultKey={defaultModelKey}
        modelValue={modelValue}
        effortValue={effortValue}
        onModelChange={onModelChange}
        onEffortChange={onEffortChange}
      />
      <SpeechLocalePicker value={speechLocale} onChange={onSpeechLocaleChange} />
    </Flex>
  );
}
