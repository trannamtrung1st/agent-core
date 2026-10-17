import { Flex, Form, Input, Tabs, Typography } from 'antd';
import type { PersonaFields } from './instanceManagedControls';

/** Persona controls shared by legacy configuration and the canonical tenant editor. */
export function InstancePersonaFields({ persona, onChange, tab, onTabChange, json, onJsonChange, error, disabled }: {
  persona: PersonaFields; onChange: (value: PersonaFields) => void; tab: string; onTabChange: (tab: string) => void;
  json: string; onJsonChange: (value: string) => void; error?: string | null; disabled: boolean;
}) {
  return <Tabs className="admin-instance-persona-tabs" activeKey={tab} onChange={onTabChange} items={[
    { key: 'form', label: 'Form', children: <Form className="admin-instance-persona-form admin-settings-form" layout="vertical" disabled={disabled}>
      <Form.Item label="Name"><Input aria-label="Persona name" maxLength={256} value={persona.name} onChange={event => onChange({ ...persona, name: event.target.value })} /></Form.Item>
      <Form.Item label="Role"><Input aria-label="Persona role" maxLength={256} value={persona.role} onChange={event => onChange({ ...persona, role: event.target.value })} /></Form.Item>
      <Form.Item label="Description"><Input.TextArea aria-label="Persona description" rows={3} maxLength={1024} value={persona.description} onChange={event => onChange({ ...persona, description: event.target.value })} /></Form.Item>
      <Form.Item label="Tone"><Input aria-label="Persona tone" maxLength={256} value={persona.tone} onChange={event => onChange({ ...persona, tone: event.target.value })} /></Form.Item>
    </Form> },
    { key: 'json', label: 'JSON', children: <Flex vertical gap="var(--ac-space-compact)"><Input.TextArea aria-label="Persona JSON" disabled={disabled} rows={8} maxLength={16384} value={json} onChange={event => onJsonChange(event.target.value)} />{error && <Typography.Text type="danger">{error}</Typography.Text>}</Flex> }
  ]} />;
}
