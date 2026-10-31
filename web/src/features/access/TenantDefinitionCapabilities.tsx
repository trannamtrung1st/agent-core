import { Button, Flex, Form, Input, Select, Switch, Typography } from 'antd';
import type { DefinitionCandidate } from '../admin/definitionCandidate';
import { AgentConfigurationPanel, AgentSettingRow } from '../admin/AgentConfigurationLayout';
import { KnowledgeResourceBinding } from '../admin/DefinitionKnowledgeBinding';
import { groupedCapabilityOptions, reconcileAlwaysCapabilities } from '../admin/capabilityOptions';
import { applyDraftExecutionPolicyToCandidate, readDraftExecutionPolicy, type DraftExecutionPolicy } from '../admin/draftExecutionPolicy';
import type { DefinitionMetadata, TenantResource } from '../../services/tenantDefinitions';

export function TenantDefinitionCapabilities({ candidate, resources, metadata, disabled, onChange }: { candidate: DefinitionCandidate; resources: TenantResource[]; metadata?: DefinitionMetadata; disabled: boolean; onChange: (value: DefinitionCandidate) => void }) {
  const executionPolicy = readDraftExecutionPolicy(candidate); const catalog = metadata?.capabilities ?? [];
  const authorized = executionPolicy.capabilityMode === 'All' ? catalog.map(c => c.name) : executionPolicy.toolAllowlist;
  const always = reconcileAlwaysCapabilities(executionPolicy.alwaysCapabilities ?? authorized, authorized, catalog);
  const update = (value: DraftExecutionPolicy) => onChange(applyDraftExecutionPolicyToCandidate(candidate, value));
  return <AgentConfigurationPanel title="Capabilities & resources" label="Draft capabilities" bodyGap="section" description="Capabilities configure the Definition. Runtime actions also require the Instance principal's own IAM permissions; these settings grant no role or approval.">
    <Form layout="vertical" disabled={disabled}>
      <AgentSettingRow label={<label htmlFor="draft-capability-selection">Capability selection</label>}><Form.Item><Select id="draft-capability-selection" aria-label="Capability selection" value={executionPolicy.capabilityMode ?? 'Selected'} options={[{ value: 'Selected', label: 'Selected capabilities' }, { value: 'All', label: 'All current capabilities on publication' }]} disabled={disabled || !metadata} onChange={mode => update({ ...executionPolicy, capabilityMode: mode, toolAllowlist: mode === 'All' ? catalog.map(c => c.name) : authorized, alwaysCapabilities: always })} /></Form.Item></AgentSettingRow>
      <Form.Item label="Authorized capabilities"><Select aria-label="Authorized capabilities" mode="multiple" showSearch value={authorized} options={groupedCapabilityOptions(catalog)} disabled={disabled || !metadata || executionPolicy.capabilityMode === 'All'} onChange={tools => update({ ...executionPolicy, capabilityMode: 'Selected', toolAllowlist: tools, alwaysCapabilities: reconcileAlwaysCapabilities(always, tools, catalog) })} /></Form.Item>
      <Form.Item label="Always projected capabilities" extra="Other discoverable capabilities remain available through progressive disclosure."><Select aria-label="Always projected capabilities" mode="multiple" value={always} options={groupedCapabilityOptions(catalog, authorized)} disabled={disabled || !metadata} onChange={names => update({ ...executionPolicy, capabilityMode: executionPolicy.capabilityMode ?? 'Selected', alwaysCapabilities: names })} /></Form.Item>
      <AgentSettingRow label={<label htmlFor="draft-workspace-template">Workspace template ID</label>} description="Bind Template resources under this ID before publishing. An ID alone does not read global files."><Form.Item><Input id="draft-workspace-template" aria-label="Workspace template ID" value={executionPolicy.workspaceTemplateId} disabled={disabled} onChange={event => update({ ...executionPolicy, workspaceTemplateId: event.target.value })} /></Form.Item></AgentSettingRow>
      <AgentSettingRow label={<label htmlFor="draft-unsupported-attachments">Allow unread unsupported attachments</label>}><Form.Item><Switch id="draft-unsupported-attachments" aria-label="Allow unread unsupported attachments" checked={executionPolicy.allowUnreadUnsupportedAttachmentTypes} disabled={disabled} onChange={value => update({ ...executionPolicy, allowUnreadUnsupportedAttachmentTypes: value })} /></Form.Item></AgentSettingRow>
    </Form>
    <Flex vertical gap="var(--ac-space-section)"><Typography.Title level={5}>Knowledge sources</Typography.Title>
      {executionPolicy.knowledgeSources.map((source, index) => <Flex vertical gap="var(--ac-space-compact)" key={index}>
        <Flex wrap gap="var(--ac-space-default)">{(['identity', 'title', 'citation'] as const).map(field => <label key={field} className="admin-draft-field" style={{ flex: '1 1 12rem' }}><Typography.Text>{field === 'identity' ? 'Knowledge identity' : field === 'title' ? 'Title' : 'Citation'}</Typography.Text><Input aria-label={`Knowledge ${field} ${index + 1}`} value={source[field]} disabled={disabled} onChange={event => update({ ...executionPolicy, knowledgeSources: executionPolicy.knowledgeSources.map((row, i) => i === index ? { ...row, [field]: event.target.value } : row) })} /></label>)}</Flex>
        <KnowledgeResourceBinding index={index} source={source} resources={resources} disabled={disabled} onChange={resourcePath => update({ ...executionPolicy, knowledgeSources: executionPolicy.knowledgeSources.map((row, i) => i === index ? { ...row, resourcePath } : row) })} />
        <Button danger disabled={disabled} style={{ alignSelf: 'start' }} onClick={() => update({ ...executionPolicy, knowledgeSources: executionPolicy.knowledgeSources.filter((_, i) => i !== index) })}>Remove source {index + 1}</Button>
      </Flex>)}
      <Button disabled={disabled} style={{ alignSelf: 'start' }} onClick={() => update({ ...executionPolicy, knowledgeSources: [...executionPolicy.knowledgeSources, { identity: '', title: '', citation: '', resourcePath: '' }] })}>Add knowledge source</Button>
    </Flex>
  </AgentConfigurationPanel>;
}
