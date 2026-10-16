import { Button, Flex, Form, Input, Select, Switch, Typography } from 'antd';
import type { DefinitionCandidate } from '../admin/definitionCandidate';
import { AgentConfigurationPanel } from '../admin/AgentConfigurationLayout';
import { KnowledgeResourceBinding } from '../admin/DefinitionKnowledgeBinding';
import { groupedCapabilityOptions, reconcileAlwaysCapabilities } from '../admin/capabilityOptions';
import { applyDraftEnvironmentToCandidate, readDraftEnvironment, type DraftEnvironment } from '../admin/draftEnvironment';
import type { DefinitionMetadata, TenantResource } from '../../services/tenantDefinitions';

export function TenantDefinitionCapabilities({ candidate, resources, metadata, disabled, onChange }: { candidate: DefinitionCandidate; resources: TenantResource[]; metadata?: DefinitionMetadata; disabled: boolean; onChange: (value: DefinitionCandidate) => void }) {
  const environment = readDraftEnvironment(candidate); const catalog = metadata?.capabilities ?? [];
  const authorized = environment.capabilityMode === 'All' ? catalog.map(c => c.name) : environment.toolAllowlist;
  const always = reconcileAlwaysCapabilities(environment.alwaysCapabilities ?? authorized, authorized, catalog);
  const update = (value: DraftEnvironment) => onChange(applyDraftEnvironmentToCandidate(candidate, value));
  return <AgentConfigurationPanel title="Capabilities & resources" label="Draft capabilities" bodyGap="section" description="Capabilities configure the Definition. Runtime actions also require the Instance principal's own IAM permissions; these settings grant no role or approval.">
    <Form layout="vertical" disabled={disabled}>
      <Form.Item label="Capability selection"><Select aria-label="Capability selection" value={environment.capabilityMode ?? 'Selected'} options={[{ value: 'Selected', label: 'Selected capabilities' }, { value: 'All', label: 'All current capabilities on publication' }]} disabled={disabled || !metadata} onChange={mode => update({ ...environment, capabilityMode: mode, toolAllowlist: mode === 'All' ? catalog.map(c => c.name) : authorized, alwaysCapabilities: always })} /></Form.Item>
      <Form.Item label="Authorized capabilities"><Select aria-label="Authorized capabilities" mode="multiple" showSearch value={authorized} options={groupedCapabilityOptions(catalog)} disabled={disabled || !metadata || environment.capabilityMode === 'All'} onChange={tools => update({ ...environment, capabilityMode: 'Selected', toolAllowlist: tools, alwaysCapabilities: reconcileAlwaysCapabilities(always, tools, catalog) })} /></Form.Item>
      <Form.Item label="Always projected capabilities" extra="Other discoverable capabilities remain available through progressive disclosure."><Select aria-label="Always projected capabilities" mode="multiple" value={always} options={groupedCapabilityOptions(catalog, authorized)} disabled={disabled || !metadata} onChange={names => update({ ...environment, capabilityMode: environment.capabilityMode ?? 'Selected', alwaysCapabilities: names })} /></Form.Item>
      <Form.Item label="Workspace template ID" extra="Bind Template resources under this ID before publishing. An ID alone does not read global files."><Input value={environment.workspaceTemplateId} disabled={disabled} onChange={event => update({ ...environment, workspaceTemplateId: event.target.value })} /></Form.Item>
      <Form.Item label="Allow unread unsupported attachments"><Switch checked={environment.allowUnreadUnsupportedAttachmentTypes} disabled={disabled} onChange={value => update({ ...environment, allowUnreadUnsupportedAttachmentTypes: value })} /></Form.Item>
    </Form>
    <Flex vertical gap="var(--ac-space-section)"><Typography.Title level={5}>Knowledge sources</Typography.Title>
      {environment.knowledgeSources.map((source, index) => <Flex vertical gap="var(--ac-space-compact)" key={index}>
        <Flex wrap gap="var(--ac-space-default)">{(['identity', 'title', 'citation'] as const).map(field => <label key={field} className="admin-draft-field" style={{ flex: '1 1 12rem' }}><Typography.Text>{field === 'identity' ? 'Knowledge identity' : field === 'title' ? 'Title' : 'Citation'}</Typography.Text><Input aria-label={`Knowledge ${field} ${index + 1}`} value={source[field]} disabled={disabled} onChange={event => update({ ...environment, knowledgeSources: environment.knowledgeSources.map((row, i) => i === index ? { ...row, [field]: event.target.value } : row) })} /></label>)}</Flex>
        <KnowledgeResourceBinding index={index} source={source} resources={resources} disabled={disabled} onChange={resourcePath => update({ ...environment, knowledgeSources: environment.knowledgeSources.map((row, i) => i === index ? { ...row, resourcePath } : row) })} />
        <Button danger disabled={disabled} style={{ alignSelf: 'start' }} onClick={() => update({ ...environment, knowledgeSources: environment.knowledgeSources.filter((_, i) => i !== index) })}>Remove source {index + 1}</Button>
      </Flex>)}
      <Button disabled={disabled} style={{ alignSelf: 'start' }} onClick={() => update({ ...environment, knowledgeSources: [...environment.knowledgeSources, { identity: '', title: '', citation: '', resourcePath: '' }] })}>Add knowledge source</Button>
    </Flex>
  </AgentConfigurationPanel>;
}
