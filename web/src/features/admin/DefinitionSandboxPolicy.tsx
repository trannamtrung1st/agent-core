import { useEffect, useState } from 'react';
import { Alert, Flex, Form, Select, Spin, Typography, theme } from 'antd';
import { getSandboxPlatform, type SandboxProfile } from '../../services/sandboxes';
import type { DefinitionCandidate } from './definitionCandidate';

type Policy = { defaultProfileId: string; allowedProfileIds: string[] };
export function DefinitionSandboxPolicy({ candidate, disabled, onChange }: { candidate: DefinitionCandidate; disabled?: boolean; onChange: (value: DefinitionCandidate) => void }) {
  const { token } = theme.useToken(); const [profiles, setProfiles] = useState<SandboxProfile[] | null>(null); const [error, setError] = useState(false);
  const policy = candidate.sandboxPolicy as Policy | null | undefined;
  useEffect(() => { const controller = new AbortController(); getSandboxPlatform(controller.signal).then(v => { if (!controller.signal.aborted) setProfiles(v.profiles); }).catch(() => { if (!controller.signal.aborted) setError(true); }); return () => controller.abort(); }, []);
  const options = [...new Map([...(profiles ?? []).map(p => [p.id, { value: p.id, label: `${p.name}${p.enabled ? '' : ' · Disabled'}` }] as const), ...(policy?.allowedProfileIds ?? []).filter(id => !profiles?.some(p => p.id === id)).map(id => [id, { value: id, label: `${id} · Unavailable` }] as const)].values()).values()];
  return <Flex vertical gap={token.paddingSM} role="group" aria-label="Definition sandbox profiles">
    <Typography.Title level={5}>Sandbox environment</Typography.Title>
    <Typography.Text type="secondary">Choose exact approved environments. Command authority comes from sandbox capabilities. New Runs pin the choice; live environments keep their existing revision until reset.</Typography.Text>
    {error && <Alert type="warning" showIcon title="Profile inventory unavailable. Existing selections are preserved; check Platform → Sandboxes." />}
    {!profiles && !error && <Spin aria-label="Loading sandbox profiles" />}
    <Form.Item label="Allowed sandbox profiles"><Select aria-label="Allowed sandbox profiles" mode="multiple" value={policy?.allowedProfileIds ?? []} options={options} disabled={disabled || !profiles} onChange={allowedProfileIds => onChange({ ...candidate, sandboxPolicy: allowedProfileIds.length ? { allowedProfileIds, defaultProfileId: policy && allowedProfileIds.includes(policy.defaultProfileId) ? policy.defaultProfileId : allowedProfileIds[0] } : null })} /></Form.Item>
    <Form.Item label="Default sandbox profile"><Select aria-label="Default sandbox profile" value={policy?.defaultProfileId} options={options.filter(o => policy?.allowedProfileIds.includes(o.value))} disabled={disabled || !policy?.allowedProfileIds.length} onChange={defaultProfileId => onChange({ ...candidate, sandboxPolicy: { ...policy, defaultProfileId } })} /></Form.Item>
  </Flex>;
}
