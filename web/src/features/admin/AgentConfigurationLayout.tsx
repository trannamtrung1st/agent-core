import type { ReactNode } from 'react';
import { Flex, Tabs, Typography, theme } from 'antd';

/** Product section shared by Admin collections and Definition/Instance configuration.
 * Owns heading rhythm and outer insets. Use bodyGap for sibling content spacing;
 * omit it for a single table/form that already owns its internal layout.
 */
export function AgentConfigurationPanel({ title, description, label, extra, children, className, bodyClassName, bodyGap }: {
  title: ReactNode; description?: ReactNode; label: string; extra?: ReactNode; children: ReactNode;
  className?: string; bodyClassName?: string; bodyGap?: 'compact' | 'default' | 'section';
}) {
  const { token } = theme.useToken();
  const gaps = { compact: token.paddingXS, default: token.paddingSM, section: token.padding };
  const bodyClasses = `admin-definition-panel-body${bodyGap ? ' admin-definition-panel-body-stack' : ''}${bodyClassName ? ` ${bodyClassName}` : ''}`;
  return <section className={`admin-definition-panel${className ? ` ${className}` : ''}`} aria-label={label}>
    <Flex vertical gap={token.paddingXS} className="admin-definition-panel-heading">
      <Flex justify="space-between" align="center" wrap gap={token.paddingXS}>
        <Typography.Title level={4}>{title}</Typography.Title>{extra}
      </Flex>
      {description && <Typography.Text type="secondary">{description}</Typography.Text>}
    </Flex>
    {bodyGap ? <Flex vertical gap={gaps[bodyGap]} className={bodyClasses}>{children}</Flex> : <div className={bodyClasses}>{children}</div>}
  </section>;
}

/** Shared configuration navigation; each owner supplies its own forms and commands. */
export function AgentIdentitySections({ profile, settings, workspace, activeKey, onChange }: {
  profile: ReactNode;
  settings: ReactNode;
  workspace?: ReactNode;
  activeKey?: string;
  onChange?: (key: string) => void;
}) {
  return <Tabs className="admin-draft-tabs admin-configuration-sections" aria-label="Identity sections"
    activeKey={activeKey} onChange={onChange} items={[
      { key: 'profile', label: 'Profile', children: profile },
      { key: 'settings', label: 'Settings', children: settings },
      ...(workspace !== undefined ? [{ key: 'workspace', label: 'Workspace', children: workspace }] : [])
    ]} />;
}

export function AgentSkillsResourcesSections({ skills, resources, activeKey, onChange }: {
  skills: ReactNode;
  resources: ReactNode;
  activeKey?: string;
  onChange?: (key: string) => void;
}) {
  return <Tabs className="admin-draft-tabs admin-configuration-sections" aria-label="Skills and resources sections"
    activeKey={activeKey} onChange={onChange} items={[
      { key: 'skills', label: 'Skills', children: skills },
      { key: 'resources', label: 'Resources', children: resources }
    ]} />;
}
