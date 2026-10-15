import { useState } from 'react';
import { Button, Flex, Form, Modal, Select, Typography, theme } from 'antd';
import type { IsolationPreset, IsolationPresetAction } from '../../services/isolationPresets';

/** One preset chooser and draft-replacement interaction across both configuration owners. */
export function IsolationPresetControl({ value, disabled, dirty, active = true, scope, useDefinitionDefaults, onSelect }: {
  value: IsolationPreset | 'Custom'; disabled: boolean; dirty: boolean; active?: boolean;
  scope: 'Definition' | 'Instance'; useDefinitionDefaults?: boolean; onSelect: (preset: IsolationPresetAction) => void;
}) {
  const { token } = theme.useToken();
  const [pending, setPending] = useState<IsolationPresetAction>();
  function select(preset: IsolationPresetAction) { if (dirty) setPending(preset); else onSelect(preset); }
  return <>
    <Modal className="isolation-preset-dialog" title="Replace unsaved isolation changes?" open={active && !!pending} centered
      okText="Replace draft" cancelText="Keep editing" onCancel={() => setPending(undefined)}
      onOk={() => { if (pending) { onSelect(pending); setPending(undefined); } }}>
      <Typography.Paragraph>The selected {pending === 'DefinitionDefaults' ? 'defaults' : 'preset'} will replace your unsaved State & Isolation draft, including Memory, Voice{scope === 'Instance' ? ', Experience and maintenance' : ''} changes. Review its preview before saving.</Typography.Paragraph>
    </Modal>
    <Flex vertical gap={token.padding}>
      <Form.Item label="Preset" extra="Session-isolated keeps multi-turn conversation within one Session. Stateless requires New chat for each request and supports text input.">
        <Select aria-label="Isolation preset" value={value} disabled={disabled}
          options={[{ value: 'Persistent', label: 'Persistent assistant' }, { value: 'SessionIsolated', label: 'Session-isolated assistant' },
            { value: 'Stateless', label: 'Stateless · new Session for each request' }, { value: 'Custom', label: 'Custom', disabled: true }]}
          onChange={next => { if (next !== 'Custom') select(next); }} />
      </Form.Item>
      {useDefinitionDefaults && <Button style={{ alignSelf: 'flex-start' }} disabled={disabled} onClick={() => select('DefinitionDefaults')}>Use Definition defaults</Button>}
    </Flex>
  </>;
}
