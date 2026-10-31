import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import { Breadcrumb, Button, Drawer, Dropdown, Flex, Layout, Tooltip, Typography, theme } from 'antd';
import { ArrowLeftOutlined, DownOutlined, MenuOutlined } from '@ant-design/icons';
import { navigateToAppPath } from '../../app/appRoute';
import { AdminNavigationIcon } from './AdminNavigationIcon';
import { followAppLink } from '../../app/appLink';
import { AdminDraftStatus } from './AdminDraftStatus';
import { type NavigationPage } from './adminNavigation';

export type AdminShellContext = {
  key: string;
  name: string;
  areaLabel: string;
  page: NavigationPage;
  navigation: (NavigationPage & { href: string })[];
  headerActions: ReactNode;
  scopeAction?: ReactNode;
  scopeNavigation?: { id: string; label: string; href: string; group: string }[];
  organizationNavigation?: { id: string; label: string; href: string; group: string }[];
  parentLink?: { label: string; href: string };
};

export function AdminShell({ headingActions, headingMetadata, context, children }: { context: AdminShellContext; headingActions?: ReactNode; headingMetadata?: ReactNode; children: ReactNode }) {
  // The Admin navigation contract uses 1024px; AntD's default lg begins at 992px.
  const [desktop, setDesktop] = useState(() => window.matchMedia('(min-width: 1024px)').matches);
  useEffect(() => {
    const media = window.matchMedia('(min-width: 1024px)');
    const update = () => setDesktop(media.matches);
    update(); media.addEventListener('change', update);
    return () => media.removeEventListener('change', update);
  }, []);
  const [menuOpen, setMenuOpen] = useState(false);
  const [dirtySources, setDirtySources] = useState<Record<string, readonly string[]>>({});
  const reportDirty = useCallback((source: string, pages: readonly string[]) => setDirtySources(current => ({ ...current, [source]: pages })), []);
  const dirtyPages = Object.values(dirtySources).flat();
  const main = useRef<HTMLDivElement>(null);
  const opener = useRef<HTMLElement | null>(null);
  const menuFocusFrame = useRef<number | null>(null);
  const cancelMenuFocus = useCallback(() => {
    if (menuFocusFrame.current !== null) cancelAnimationFrame(menuFocusFrame.current);
    menuFocusFrame.current = null;
  }, []);
  useEffect(() => cancelMenuFocus, [cancelMenuFocus]);
  const menuNavigation = useRef(false);
  const keyboardNavigation = useRef(false);
  useEffect(() => {
    const keyboard = () => { keyboardNavigation.current = true; };
    const pointer = () => { keyboardNavigation.current = false; };
    window.addEventListener('keydown', keyboard, true); window.addEventListener('pointerdown', pointer, true);
    return () => { window.removeEventListener('keydown', keyboard, true); window.removeEventListener('pointerdown', pointer, true); };
  }, []);
  const active = context.page;
  const items = context.navigation;
  const routeKey = context.key;
  useEffect(() => {
    if (menuOpen) menuNavigation.current = true;
    setMenuOpen(false);
    const frame = requestAnimationFrame(() => {
      if (window.location.hash) return; // The section owns focus once its asynchronous content mounts.
      main.current?.scrollTo?.(0, 0);
      if (keyboardNavigation.current) main.current?.querySelector<HTMLElement>('[data-admin-page-heading]')?.focus({ preventScroll: true });
    });
    return () => cancelAnimationFrame(frame);
  }, [routeKey]);
  const nav = <nav aria-label="Admin navigation" className="admin-navigation">
    {context.parentLink && <a className="admin-nav-back" href={context.parentLink.href} onClick={followAppLink}><ArrowLeftOutlined aria-hidden />{context.parentLink.label}</a>}
    {context.organizationNavigation && <Dropdown trigger={['click']} menu={{ items: context.organizationNavigation.map(p => ({ key: p.href, label: p.label })), onClick: ({ key }) => navigateToAppPath(key) }}><Button icon={<MenuOutlined aria-hidden />}>Organization navigation <DownOutlined aria-hidden /></Button></Dropdown>}
    {[...new Set(items.map(p => p.group))].map(group => <details className="admin-nav-group" key={`${context.areaLabel}:${group}:${context.parentLink?.href ?? ''}`} open={group === active.group || group === 'Work' || group === 'Identity' || group === 'Account' || group === 'Platform' || group === ''}>
      <summary className="admin-nav-group-title"><DownOutlined aria-hidden className="admin-nav-chevron" /><span>{group || 'Overview'}</span></summary>
      {items.filter(p => p.group === group).map(p => <Tooltip key={p.id} title={dirtyPages.includes(p.id) ? 'Unsaved changes' : undefined} placement="right" trigger={['hover', 'focus']}><a href={p.href} aria-label={p.label} aria-description={dirtyPages.includes(p.id) ? "Unsaved changes" : undefined} aria-current={active?.id === p.id ? 'page' : undefined} className="admin-nav-link" onClick={event => { followAppLink(event); if (event.defaultPrevented) { menuNavigation.current = true; setMenuOpen(false); } }}><AdminNavigationIcon id={p.id} /><span className="admin-nav-label">{p.label}</span>{dirtyPages.includes(p.id) && <span className="admin-nav-unsaved" aria-hidden="true" />}</a></Tooltip>)}
    </details>)}
  </nav>;
  return <AdminDraftStatus.Provider value={reportDirty}><Layout className="admin-layout access-layout">
    <Layout.Header className="admin-header">
      <Flex align="center" wrap gap="var(--ac-space-compact)" className="admin-header-inner">
        {!desktop && <Button icon={<MenuOutlined aria-hidden />} aria-label="Open Admin navigation" onClick={e => { cancelMenuFocus(); opener.current = e.currentTarget; menuNavigation.current = false; setMenuOpen(true); }} />}
        <Flex align="center" gap="var(--ac-space-compact)" className="admin-brand"><Typography.Text strong className="admin-title">Agent Core</Typography.Text></Flex>
        <Breadcrumb aria-label={`Admin breadcrumb · ${context.areaLabel}`} className="admin-breadcrumbs" items={[
          { title: <span className="admin-breadcrumb-owner" title={context.name}>{context.scopeAction ?? context.name}</span> },
          { title: active?.label ?? 'Page not found' },
        ]} />
        <Flex wrap gap="var(--ac-space-compact)" className="admin-header-actions">
          {!!context.scopeNavigation?.length && <Dropdown trigger={['click']} menu={{ items: context.scopeNavigation.map(p => ({ key: p.href, label: p.label })), onClick: ({ key }) => navigateToAppPath(key) }}><Button aria-label="Account and platform navigation" icon={<DownOutlined aria-hidden />}><span className="admin-scope-label">Account</span></Button></Dropdown>}
          {context.headerActions}
        </Flex>
      </Flex>
    </Layout.Header>
    <div className="admin-workspace">
      {desktop && <aside className="admin-sidebar">{nav}</aside>}
      <Layout.Content className="admin-content" ref={main}>
        <Flex vertical gap="var(--ac-space-section)">
          <AdminPageHeading page={active} extra={headingActions} metadata={headingMetadata} />
          {children}
        </Flex>
      </Layout.Content>
    </div>
    <Drawer rootClassName="admin-navigation-drawer" title="Admin navigation" styles={{ body: { padding: 0 } }} open={menuOpen && !desktop} placement="left" size={280} focusable={{ trap: true, focusTriggerAfterClose: false }} onClose={() => setMenuOpen(false)} afterOpenChange={open => {
      cancelMenuFocus();
      // Drawer invokes this during React's commit. Moving focus can blur a
      // Tooltip trigger, which synchronously updates its popup state.
      if (!open) menuFocusFrame.current = requestAnimationFrame(() => {
        menuFocusFrame.current = null;
        if (menuNavigation.current && keyboardNavigation.current) {
          if (!window.location.hash) main.current?.querySelector<HTMLElement>('[data-admin-page-heading]')?.focus({ preventScroll: true });
        } else opener.current?.focus({ preventScroll: true });
      });
    }}>{nav}</Drawer>
  </Layout></AdminDraftStatus.Provider>;
}

export function AdminPageHeading({ page, metadata, extra }: { page?: NavigationPage; metadata?: ReactNode; extra?: ReactNode }) {
  return <div className="admin-page-heading">
    <Flex justify="space-between" align="center" wrap gap="var(--ac-space-default)">
      <Flex align="baseline" wrap gap="var(--ac-space-compact)"><Typography.Title level={1} tabIndex={-1} data-admin-page-heading>{page?.label ?? 'Page not found'}</Typography.Title>{metadata && <Typography.Text type="secondary">{metadata}</Typography.Text>}</Flex>
      {extra}
    </Flex>
    {page && <Typography.Paragraph type="secondary">{page.description}</Typography.Paragraph>}
  </div>;
}

/** The selected owner is secondary context; the route heading names the current task. */
export function AdminOwnerHeading({ name, metadata, status, extra, notice }: { name: ReactNode; metadata?: ReactNode; status?: ReactNode; extra?: ReactNode; notice?: ReactNode }) {
  const { token } = theme.useToken();
  return <div className="admin-definition-heading admin-owner-heading">
    <Flex justify="space-between" align="start" wrap gap={token.paddingSM}>
      <Flex align="center" wrap gap={token.paddingXS} className="admin-owner-title">
        <Typography.Title level={3} style={{ fontSize: token.fontSizeLG, lineHeight: token.lineHeightLG }}>{name}</Typography.Title>
        {status}
      </Flex>
      {extra}
    </Flex>
    {metadata && <Flex wrap gap={token.paddingSM} className="admin-owner-metadata">{metadata}</Flex>}
    {notice}
  </div>;
}
