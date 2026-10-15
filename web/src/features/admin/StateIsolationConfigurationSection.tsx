import { Alert, Button, Flex, Form, Spin, Typography, theme } from 'antd';
import { StateIsolationPolicyFields, StateIsolationToggleField } from './StateIsolationPolicyFields';
import { AgentConfigurationPanel } from './AgentConfigurationLayout';
import { deriveIsolationPreset, isolationValue } from '../../services/stateIsolation';
import { followAppLink } from '../../app/appLink';
import { instancePath } from './adminNavigation';
import { isolationLabels as labels } from './stateIsolationPresentation';
import { IsolationPresetControl } from './IsolationPresetControl';
import { IsolationChangePreview } from './IsolationChangePreview';
import { StateIsolationConfigurationProvider, useStateIsolationConfiguration } from './StateIsolationConfigurationProvider';

export function StateIsolationConfigurationSection(props: {
  instanceId: string; active: boolean; archived: boolean; onUpdated: () => void; onDirtyChange: (dirty: boolean) => void;
}) {
  const shared = useStateIsolationConfiguration();
  return shared ? <IsolationFields {...props} /> : <StateIsolationConfigurationProvider {...props}><IsolationFields {...props} /></StateIsolationConfigurationProvider>;
}

function IsolationFields({ instanceId, active, archived }: { instanceId: string; active: boolean; archived: boolean }) {
  const { token } = theme.useToken();
  const { view, draft, plan, error, busy, previewing, changed, editor, refresh, save, editFlag, edit, choose } = useStateIsolationConfiguration()!;
  const state = view?.settings.find(s => s.section === 'stateIsolationPolicy');
  return <Flex vertical gap={token.padding}>
    {error && <Alert type="error" showIcon title={error} action={<Button onClick={() => void refresh().catch(() => {})}>Retry isolation</Button>} />}
    {!view && busy && <Spin aria-label="Loading State & Isolation" />}
    {view && draft && state && <>
      <AgentConfigurationPanel title="Continuity and resource policy" label="State & Isolation configuration" bodyGap="section"
        description="Presets prepare editable drafts. Existing history, home files and browser profile bytes are retained.">
        <Form layout="vertical" className="admin-config-form instance-settings-form">
          <Flex vertical gap={token.padding}>
            <IsolationPresetControl value={deriveIsolationPreset(view, draft)} disabled={archived || busy} dirty={changed}
              active={active} scope="Instance" useDefinitionDefaults onSelect={preset => void choose(preset)} />
            <StateIsolationPolicyFields value={field => isolationValue(view, draft, state.section, field)}
              choices={field => state.constraints?.[field]?.choices ?? []}
              disabled={field => archived || busy || state.constraints?.[field]?.requiredBoolean === false}
              onChange={(field, value) => edit('stateIsolationPolicy', field, value)} extra={field => {
              const patch = draft.settings.find(p => p.section === state.section);
              const source = patch?.clear.includes(field) ? 'Will inherit on save' : patch && field in patch.set ? 'Unsaved Instance override' : state.sources[field] === 'instance' ? 'Instance override' : 'Inherited Definition';
              return <Flex wrap align="center" gap={token.paddingXS}><Typography.Text type="secondary">{source}</Typography.Text>
                {!patch?.clear.includes(field) && (field in state.overrides || patch && field in patch.set) && <Button size="small" type="link" disabled={archived || busy} aria-label={`Inherit ${labels[field] ?? field}`} onClick={() => edit('stateIsolationPolicy', field, undefined)}>Inherit</Button>}</Flex>;
            }}>
              <StateIsolationToggleField label="Experience enablement" checked={draft.experienceEnabled} disabled={archived || busy}
                extra="The existing Experience owner is updated in the same transaction." onChange={v => editFlag('experienceEnabled', v)} />
              <StateIsolationToggleField label="Autonomous identity maintenance" checked={draft.allowMaintenance} disabled={archived || busy}
                extra="The existing maintenance owner is updated in the same transaction." onChange={v => editFlag('allowMaintenance', v)} />
            </StateIsolationPolicyFields>
          </Flex>
        </Form>
        <Flex wrap gap={token.paddingSM}>{(['memory-policy', 'voice', 'experience', 'memory', 'credentials'] as const).map(page => <a key={page} href={instancePath(instanceId, page)} onClick={followAppLink}>{page === 'memory-policy' ? 'Edit detailed Memory policy' : page === 'voice' ? 'Edit Voice settings' : page === 'credentials' ? 'Inspect shared credentials' : page === 'memory' ? 'Inspect learned Memory' : 'Inspect Experience'}</a>)}</Flex>
        <Typography.Text type="secondary">Shared email, Drive and other bound accounts retain their own authority and external effects. Conversation isolation does not isolate those accounts.</Typography.Text>
      </AgentConfigurationPanel>
      <IsolationChangePreview changes={plan?.changes} notice={plan?.notice} blockingReasons={plan?.blockingReasons} busy={previewing}>
        {!editor.managed && <Button type="primary" disabled={archived || busy || previewing || !changed || !plan || !!plan.blockingReasons.length} onClick={() => { if (view) void save(view.revisions.instance).catch(() => {}); }}>Apply changes</Button>}
      </IsolationChangePreview>
    </>}
  </Flex>;
}
