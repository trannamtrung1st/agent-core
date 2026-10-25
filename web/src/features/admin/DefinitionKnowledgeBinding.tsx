import { Select, Typography } from "antd";
import type { DraftKnowledgeSource } from "./draftExecutionPolicy";

function knowledgeBackingLabel(identity: string, resourcePath: string) {
  const selected = resourcePath.trim();
  if (selected.length > 0) {
    return `Backing resource: ${selected}`;
  }
  const legacyIdentity = identity.trim() || "{identity}";
  return `Default binding: knowledge/${legacyIdentity}`;
}

export function KnowledgeResourceBinding({
  index,
  source,
  resources,
  disabled,
  onChange
}: {
  index: number;
  source: DraftKnowledgeSource;
  resources: { logicalPath: string; kind: string | number }[];
  disabled: boolean;
  onChange: (resourcePath: string) => void;
}) {
  const knowledgePaths = resources
    .filter((item) => (item.kind === "Knowledge" || item.kind === 1))
    .map((item) => item.logicalPath);
  const selected = source.resourcePath.trim();
  const options = [
    { value: "", label: knowledgeBackingLabel(source.identity, "") },
    ...knowledgePaths.map((path) => ({ value: path, label: path })),
    ...(selected.length > 0 && !knowledgePaths.includes(selected)
      ? [{ value: selected, label: `${selected} (not in this draft)` }]
      : [])
  ];

  return (
    <label className="admin-draft-field">
      <Typography.Text>Backing resource</Typography.Text>
      <Select
        aria-label={`Knowledge resource ${index + 1}`}
        value={selected}
        options={options}
        onChange={onChange}
        disabled={disabled}
      />
      <Typography.Text type="secondary">{knowledgeBackingLabel(source.identity, selected)}</Typography.Text>
    </label>
  );
}
