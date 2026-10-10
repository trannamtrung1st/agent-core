import { AgentConfigurationPanel } from './AgentConfigurationLayout';
import { DeleteOutlined, EditOutlined, EyeOutlined } from '@ant-design/icons';
import { useState } from 'react';
import { Button, Empty, Flex, Grid, Table, Tag, Tooltip, Typography, theme } from 'antd';
import { readSkills, writeSkills, skillIdFromName, type DefinitionCandidate } from './definitionCandidate';
import { SkillDrawer, type SkillDrawerValue } from './SkillDrawer';

export function DefinitionSkillsSection({ candidate, busy, readOnly, onChange }: {
  candidate: DefinitionCandidate; busy: boolean; readOnly: boolean; onChange: (candidate: DefinitionCandidate) => void;
}) {
  const { token } = theme.useToken();
  const skills = readSkills(candidate);
  const compact = !Grid.useBreakpoint().md;
  const [selected, setSelected] = useState<{ index: number | null; details: boolean; value: SkillDrawerValue } | null>(null);
  function open(index: number | null, details = false) {
    const skill = index === null ? null : skills[index];
    setSelected({ index, details, value: skill ? { ...skill, enabled: skill.defaultEnabled, capabilities: skill.requiredCapabilities } : {
      id: '', name: '', description: '', procedure: '', projection: 'OnDemand', enabled: true, capabilities: '', resourcePaths: ''
    } });
  }
  function save(value: SkillDrawerValue) {
    if (!selected || busy || readOnly) return;
    const next = { id: value.id?.trim() || skillIdFromName(value.name, skills.filter((_, index) => index !== selected.index).map(s => s.id)), name: value.name, description: value.description, procedure: value.procedure,
      projection: value.projection, defaultEnabled: value.enabled, requiredCapabilities: value.capabilities, resourcePaths: value.resourcePaths ?? '' };
    onChange(writeSkills(candidate, selected.index === null ? [...skills, next] : skills.map((skill, index) => index === selected.index ? next : skill)));
    setSelected(null);
  }
  function remove(index: number) {
    const existing = Array.isArray(candidate.skills) ? candidate.skills : [];
    onChange(writeSkills({ ...candidate, skills: existing.filter((_, i) => i !== index) }, skills.filter((_, i) => i !== index)));
  }
  return <AgentConfigurationPanel title="Skills" label="Skills"
    description="Skills are procedures for this definition. Required capabilities are requirements, not grants. They do not add tools, credentials, or approval."
    extra={!readOnly && <Button disabled={busy} onClick={() => open(null)}>Add skill</Button>}>
    <Table className="admin-collection-table admin-skills-table" tableLayout="fixed" size="small" pagination={false} rowKey="index" scroll={{ x: compact ? (readOnly ? 514 : 500) : readOnly ? 1120 : 1300 }}
      dataSource={skills.map((skill, index) => ({ ...skill, index }))}
      locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={readOnly ? "This published version has no skills." : "No skills yet. A definition without skills runs with an empty skill set."} /> }}
      columns={[
        { title: 'Skill', width: compact ? (readOnly ? 270 : 160) : 200, onCell: () => ({ style: { whiteSpace: 'normal' } }), render: (_, skill) => <Flex vertical gap={token.paddingXS}>
          <Typography.Text strong className="admin-skill-summary" ellipsis={{ tooltip: true }} title={skill.name || 'Untitled skill'}>{skill.name || 'Untitled skill'}</Typography.Text>
          {compact ? <>
            <Typography.Text className="admin-skill-summary" ellipsis={{ tooltip: true }} title={skill.description}>{skill.description}</Typography.Text>
            <Flex wrap gap={token.paddingXS}><Tag>{skill.projection === 'Always' ? 'Always' : skill.projection === 'OnDemand' ? 'On demand' : 'Not set'}</Tag></Flex>
            <Typography.Text type="secondary">Default enabled: {skill.defaultEnabled === undefined ? 'Not set' : skill.defaultEnabled ? 'Yes' : 'No'}</Typography.Text>
            <Typography.Text type="secondary" className="admin-skill-summary" ellipsis={{ tooltip: true }}>Requires: {skill.requiredCapabilities || 'No capabilities'}</Typography.Text>
          </> : null}
        </Flex> },
        { title: 'Skill ID', width: 180, onCell: () => ({ style: { whiteSpace: 'normal' } }), render: (_, skill) => <Typography.Text type="secondary" className="admin-skill-summary" ellipsis={{ tooltip: true }} title={skill.id || 'Not set'}>{skill.id || 'Not set'}</Typography.Text> },
        { title: 'Description', width: 240, responsive: ['md'], onCell: () => ({ style: { whiteSpace: 'normal' } }), render: (_, skill) => <Typography.Text className="admin-skill-summary" ellipsis={{ tooltip: true }} title={skill.description}>{skill.description}</Typography.Text> },
        { title: 'Activation', width: 120, responsive: ['md'], render: (_, skill) => <Tag>{skill.projection === 'Always' ? 'Always' : skill.projection === 'OnDemand' ? 'On demand' : 'Not set'}</Tag> },
        { title: 'Default enabled', width: 110, responsive: ['md'], render: (_, skill) => skill.defaultEnabled === undefined ? 'Not set' : skill.defaultEnabled ? 'Yes' : 'No' },
        { title: 'Requires', width: 170, responsive: ['md'], onCell: () => ({ style: { whiteSpace: 'normal' } }), render: (_, skill) => <Typography.Text className="admin-skill-summary" ellipsis={{ tooltip: true }} title={skill.requiredCapabilities || 'No capabilities'}>{skill.requiredCapabilities || 'No capabilities'}</Typography.Text> },
        { title: 'Actions', fixed: 'right', width: compact ? (readOnly ? 64 : 160) : readOnly ? 100 : 280, render: (_, skill) => <Flex className="admin-table-actions" gap={token.paddingXS}>
          <Tooltip title={compact ? 'Details' : undefined}><Button aria-label="Details" icon={compact ? <EyeOutlined /> : undefined} disabled={busy} onClick={() => open(skill.index, true)}>{compact ? null : 'Details'}</Button></Tooltip>
          {!readOnly && <><Tooltip title={compact ? 'Edit' : undefined}><Button aria-label="Edit" icon={compact ? <EditOutlined /> : undefined} disabled={busy} onClick={() => open(skill.index)}>{compact ? null : 'Edit'}</Button></Tooltip>
            <Tooltip title={compact ? 'Remove' : undefined}><Button icon={compact ? <DeleteOutlined /> : undefined} danger disabled={busy} aria-label={`Remove skill ${skill.index + 1}`} onClick={() => remove(skill.index)}>{compact ? null : 'Remove'}</Button></Tooltip></>}
        </Flex> }
      ]} />
    <SkillDrawer open={Boolean(selected)} definition value={selected?.value ?? null}
      title={selected?.details ? 'Definition Skill details' : selected?.index === null ? 'New Definition Skill' : 'Edit Definition Skill'}
      readOnly={readOnly || selected?.details} busy={busy} contentLabel="Definition Skill content"
      context={selected?.details || readOnly ? 'Reusable procedure for this Definition. Required capabilities do not grant authority.' : 'Save the Skill to this draft, then save the draft before using Test & Publish.'}
      validateId={id => id && skills.some((skill, index) => index !== selected?.index && skill.id === id) ? 'This Skill ID already exists in this Definition.' : null}
      onSave={save} onClose={() => { if (!busy) setSelected(null); }} />
  </AgentConfigurationPanel>;
}
