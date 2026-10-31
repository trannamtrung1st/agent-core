import { RetainedAdminPage } from './RetainedAdminPage';
import { routeTabLabel } from "../../app/appLink";
import { useId, type ReactNode } from 'react';
import { Flex, Tabs, Typography, theme } from 'antd';

/** Product section shared by Admin collections and Definition/Instance configuration.
 * Owns heading rhythm and outer insets. Use bodyGap for sibling content spacing;
 * omit it for a single table/form that already owns its internal layout.
 */
export function AgentConfigurationPanel({ title, description, label, extra, children, className, bodyClassName, bodyGap, id }: {
  title: ReactNode; description?: ReactNode; label: string; extra?: ReactNode; children: ReactNode;
  id?: string; className?: string; bodyClassName?: string; bodyGap?: 'compact' | 'default' | 'section';
}) {
  const { token } = theme.useToken();
  const gaps = { compact: token.paddingXS, default: token.paddingSM, section: token.padding };
  const bodyClasses = `admin-definition-panel-body${bodyGap ? ' admin-definition-panel-body-stack' : ''}${bodyClassName ? ` ${bodyClassName}` : ''}`;
  return <section id={id} tabIndex={id ? -1 : undefined} className={`admin-definition-panel${className ? ` ${className}` : ''}`} aria-label={label}>
    <Flex vertical gap={token.paddingXS} className="admin-definition-panel-heading">
      <Flex justify="space-between" align="center" wrap gap={token.paddingXS}>
        <Typography.Title level={4}>{title}</Typography.Title>{extra}
      </Flex>
      {description && <Typography.Text type="secondary">{description}</Typography.Text>}
    </Flex>
    {bodyGap ? <Flex vertical gap={gaps[bodyGap]} className={bodyClasses}>{children}</Flex> : <div className={bodyClasses}>{children}</div>}
  </section>;
}

/** A descriptive settings row. Persistence and native form validation remain at the caller. */
export function AgentSettingRow({ label, description, children, fullWidth = false }: {
  label: ReactNode; description?: ReactNode; children: ReactNode; fullWidth?: boolean;
}) {
  const id = useId();
  return <div role="group" aria-describedby={description ? `${id}-description` : undefined} className={`agent-setting-row${fullWidth ? ' agent-setting-row-wide' : ''}`}>
    <div className="agent-setting-description"><div id={`${id}-label`} className="agent-setting-label">{label}</div>
      {description && <Typography.Text id={`${id}-description`} type="secondary">{description}</Typography.Text>}
    </div>
    <div className="agent-setting-control">{children}</div>
  </div>;
}

/** Shared configuration navigation; each owner supplies its own forms and commands. */
export function AgentIdentitySections({ profile, settings, workspace, activeKey, onChange, sectionHref, section }: {
  profile: ReactNode;
  settings: ReactNode;
  workspace?: ReactNode;
  activeKey?: string;
  onChange?: (key: string) => void;
  sectionHref?: (key: string) => string;
  section?: string;
}) {
  if (section) return <>{section === "profile" ? profile : settings}</>;
  return <Tabs className="admin-draft-tabs admin-configuration-sections" aria-label="Identity sections"
    activeKey={activeKey} onChange={onChange} items={[
      { key: 'profile', label: sectionHref ? routeTabLabel('Profile', sectionHref('profile'), () => onChange?.('profile')) : 'Profile', children: profile },
      { key: 'settings', label: sectionHref ? routeTabLabel('Settings', sectionHref('settings'), () => onChange?.('settings')) : 'Settings', children: settings },
      ...(workspace !== undefined ? [{ key: 'workspace', label: sectionHref ? routeTabLabel('Workspace', sectionHref('workspace'), () => onChange?.('workspace')) : 'Workspace', children: workspace }] : [])
    ]} />;
}

export function AgentSkillsResourcesSections({ skills, resources, activeKey, onChange, sectionHref, section }: {
  skills: ReactNode;
  resources: ReactNode;
  activeKey?: string;
  onChange?: (key: string) => void;
  sectionHref?: (key: string) => string;
  section?: string;
}) {
  if (section) return <><RetainedAdminPage active={section === "skills"}>{skills}</RetainedAdminPage><RetainedAdminPage active={section === "resources"}>{resources}</RetainedAdminPage></>;
  return <Tabs className="admin-draft-tabs admin-configuration-sections" aria-label="Skills and resources sections"
    activeKey={activeKey} onChange={onChange} items={[
      { key: 'skills', label: sectionHref ? routeTabLabel('Skills', sectionHref('skills'), () => onChange?.('skills')) : 'Skills', children: skills },
      { key: 'resources', label: sectionHref ? routeTabLabel('Resources', sectionHref('resources'), () => onChange?.('resources')) : 'Resources', children: resources }
    ]} />;
}
