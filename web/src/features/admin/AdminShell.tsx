import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import { Breadcrumb, Button, Drawer, Empty, Flex, Input, Layout, Modal, Typography, theme } from 'antd';
import { ArrowLeftOutlined, MenuOutlined, MessageOutlined, SearchOutlined } from '@ant-design/icons';
import { followAppLink } from '../../app/appLink';
import { adminDefinitionPath, adminDraftPath, adminHomePath, adminInstancePath, lastChatUrl, type AdminRoute } from '../../app/appRoute';
import { AdminDraftStatus } from './AdminDraftStatus';
import { definitionPages, draftPages, globalPages, instancePages, settingSearch, type DraftPage, type NavigationPage } from './adminNavigation';

export function adminNavigationFor(route: AdminRoute) {
  if (route.view === 'instance') return instancePages.map(p => ({ ...p, href: adminInstancePath(route.instanceId, p.id) }));
  if (route.view === 'draft') return draftPages.map(p => ({ ...p, href: adminDraftPath(route.definitionId, route.draftId, p.id) }));
  if (route.view === 'definition') return definitionPages.map(p => ({ ...p, href: adminDefinitionPath(route.definitionId, p.id) }));
  return globalPages.map(p => ({ ...p, href: adminHomePath(p.id) }));
}
export function adminPage(route: AdminRoute): NavigationPage | undefined {
  const id = route.view === 'instance' || route.view === 'draft' ? route.page : route.view === 'definition' ? route.tab ?? 'versions' : route.view === 'home' ? route.collection ?? 'instances' : '';
  return adminNavigationFor(route).find(p => p.id === id);
}

export function AdminShell({ route, ownerName, headingActions, headingMetadata, children }: { route: AdminRoute; ownerName?: string; headingActions?: ReactNode; headingMetadata?: ReactNode; children: ReactNode }) {
  // The Admin navigation contract uses 1024px; AntD's default lg begins at 992px.
  const [desktop, setDesktop] = useState(() => window.matchMedia('(min-width: 1024px)').matches);
  useEffect(() => {
    const media = window.matchMedia('(min-width: 1024px)');
    const update = () => setDesktop(media.matches);
    update(); media.addEventListener('change', update);
    return () => media.removeEventListener('change', update);
  }, []);
  const [menuOpen, setMenuOpen] = useState(false);
  const [searchOpen, setSearchOpen] = useState(false);
  const [dirtySources, setDirtySources] = useState<Record<string, readonly string[]>>({});
  const reportDirty = useCallback((source: string, pages: readonly string[]) => setDirtySources(current => ({ ...current, [source]: pages })), []);
  const dirtyPages = Object.values(dirtySources).flat();
  const [query, setQuery] = useState('');
  const main = useRef<HTMLDivElement>(null);
  const opener = useRef<HTMLElement | null>(null);
  const menuNavigation = useRef(false);
  const searchOpener = useRef<HTMLElement | null>(null);
  const searchNavigation = useRef(false);
  const keyboardNavigation = useRef(false);
  useEffect(() => {
    const keyboard = () => { keyboardNavigation.current = true; };
    const pointer = () => { keyboardNavigation.current = false; };
    window.addEventListener('keydown', keyboard, true); window.addEventListener('pointerdown', pointer, true);
    return () => { window.removeEventListener('keydown', keyboard, true); window.removeEventListener('pointerdown', pointer, true); };
  }, []);
  const active = adminPage(route);
  const items = adminNavigationFor(route);
  const scoped = route.view === 'instance' || route.view === 'definition' || route.view === 'draft';
  const collection = route.view === 'instance' ? 'instances' : 'definitions';
  const owner = ownerName ?? (route.view === 'instance' ? `Instance ${route.instanceId.slice(0, 8)}` : route.view === 'definition' || route.view === 'draft' ? route.definitionId : '');
  const routeKey = route.view === 'instance' ? `${route.instanceId}/${route.page}` : route.view === 'draft' ? `${route.draftId}/${route.page}` : route.view === 'definition' ? `${route.definitionId}/${route.tab}` : route.view === 'home' ? route.collection : 'not-found';
  useEffect(() => {
    if (menuOpen) menuNavigation.current = true;
    setMenuOpen(false); setSearchOpen(false);
    const frame = requestAnimationFrame(() => {
      if (window.location.hash) return; // The section owns focus once its asynchronous content mounts.
      main.current?.scrollTo?.(0, 0);
      if (keyboardNavigation.current) main.current?.querySelector<HTMLElement>('[data-admin-page-heading]')?.focus({ preventScroll: true });
    });
    return () => cancelAnimationFrame(frame);
  }, [routeKey]);
  const nav = <nav aria-label="Admin navigation" className="admin-navigation">
    {scoped && <a className="admin-nav-back" href={adminHomePath(collection)} onClick={followAppLink}><ArrowLeftOutlined aria-hidden /> Back to {collection === 'instances' ? 'Instances' : 'Definitions'}</a>}
    {scoped && <Typography.Text strong className="admin-nav-owner" title={owner}>{owner}</Typography.Text>}
    {route.view === 'draft' && <a className="admin-nav-back" href={adminDefinitionPath(route.definitionId, 'drafts')} onClick={followAppLink}>Back to drafts</a>}
    {[...new Set(items.map(p => p.group))].map(group => <div className="admin-nav-group" key={group}>
      {group && <Typography.Text type="secondary" className="admin-nav-group-title">{group}</Typography.Text>}
      {items.filter(p => p.group === group).map(p => <a key={p.id} href={p.href} aria-label={p.label} aria-description={dirtyPages.includes(p.id) ? "Unsaved changes" : undefined} aria-current={active?.id === p.id ? 'page' : undefined} className="admin-nav-link" onClick={event => { followAppLink(event); if (event.defaultPrevented) { menuNavigation.current = true; setMenuOpen(false); } }}>{p.label}{dirtyPages.includes(p.id) && <span className="admin-nav-unsaved" aria-hidden="true"> · Unsaved</span>}</a>)}
    </div>)}
    {scoped && <a className="admin-nav-back" href={adminHomePath('credentials')} onClick={followAppLink}>Global integrations & platform</a>}
  </nav>;
  const fieldResults = route.view === 'instance' || route.view === 'draft' ? settingSearch.filter(p => route.view === 'instance' ? !('draftOnly' in p) : !('instanceOnly' in p)).map(p => ({
    id: `setting:${p.label}`, label: p.label, description: 'Setting · ' + (route.view === 'instance' ? instancePages.find(item => item.id === p.page)?.label : draftPages.find(item => item.id === ('draftPage' in p ? p.draftPage : p.page))?.label), aliases: p.aliases, scope: 'owner' as const,
    href: route.view === 'instance' ? adminInstancePath(route.instanceId, p.page) + (p.anchor ? `#${p.anchor}` : '') : adminDraftPath(route.definitionId, route.draftId, ('draftPage' in p ? p.draftPage : p.page) as DraftPage) + (('draftAnchor' in p ? p.draftAnchor : p.anchor) ? `#${'draftAnchor' in p ? p.draftAnchor : p.anchor}` : ''),
  })) : [];
  const searchItems = [
    ...(scoped ? items.map(p => ({ ...p, scope: 'owner' as const })) : []),
    ...fieldResults,
    ...globalPages.map(p => ({ ...p, href: adminHomePath(p.id), scope: 'global' as const })),
  ].filter(p => `${p.label} ${p.description} ${p.aliases.join(' ')}`.toLowerCase().includes(query.trim().toLowerCase()));
  const searchGroups = [
    { id: 'owner', label: route.view === 'draft' ? `Draft ${route.draftId.slice(0, 8)} · ${owner}` : `${route.view === 'instance' ? 'Instance' : 'Definition'} · ${owner}`, items: searchItems.filter(p => p.scope === 'owner') },
    { id: 'global', label: 'Global Admin', items: searchItems.filter(p => p.scope === 'global') },
  ].filter(group => group.items.length > 0);
  return <AdminDraftStatus.Provider value={reportDirty}><Layout className="admin-layout">
    <Layout.Header className="admin-header">
      <Flex align="center" wrap gap="var(--ac-space-compact)" className="admin-header-inner">
        {!desktop && <Button icon={<MenuOutlined aria-hidden />} aria-label="Open Admin navigation" onClick={e => { opener.current = e.currentTarget; menuNavigation.current = false; setMenuOpen(true); }} />}
        <Typography.Title level={3} className="admin-title">Admin</Typography.Title>
        <Breadcrumb aria-label="Admin breadcrumb" className={`admin-breadcrumbs${scoped ? ' admin-breadcrumbs-scoped' : ''}`} items={[
          { title: <a href={adminHomePath()} onClick={followAppLink}>Admin</a> },
          ...(scoped ? [{ title: <a href={adminHomePath(collection)} onClick={followAppLink}>{collection === 'instances' ? 'Instances' : 'Definitions'}</a> }, { title: <span className="admin-breadcrumb-owner" title={owner}>{owner}</span> }] : []),
          ...(route.view === 'draft' ? [{ title: `Draft ${route.draftId.slice(0, 8)}` }] : []),
          { title: active?.label ?? 'Page not found' },
        ]} />
        <Flex wrap gap="var(--ac-space-compact)" className="admin-header-actions">
          <Button className="admin-header-search" icon={<SearchOutlined aria-hidden />} aria-label="Search Admin" onClick={event => { searchOpener.current = event.currentTarget; searchNavigation.current = false; setQuery(''); setSearchOpen(true); }}><span className="admin-search-label">Search Admin</span></Button>
          <Button className="admin-header-chat" type="text" icon={<MessageOutlined aria-hidden />} href="/" onClick={followAppLink} aria-label="Chat"><span className="admin-chat-label">Chat</span></Button>
          <Button className="admin-header-return" icon={<ArrowLeftOutlined aria-hidden />} href={lastChatUrl()} onClick={followAppLink} aria-label="Return to last chat"><span className="admin-return-label-wide">Return to last chat</span><span className="admin-return-label-compact">Return</span></Button>
        </Flex>
      </Flex>
    </Layout.Header>
    <div className="admin-workspace">
      {desktop && <aside className="admin-sidebar">{nav}</aside>}
      <Layout.Content className="admin-content" ref={main}>
        <Flex vertical gap="var(--ac-space-section)">
          {(route.view === 'home' || route.view === 'not-found') && <AdminPageHeading page={active} extra={headingActions} metadata={headingMetadata} />}
          {children}
        </Flex>
      </Layout.Content>
    </div>
    <Drawer rootClassName="admin-navigation-drawer" title="Admin navigation" styles={{ body: { padding: 0 } }} open={menuOpen && !desktop} placement="left" size={280} focusable={{ trap: true, focusTriggerAfterClose: false }} onClose={() => setMenuOpen(false)} afterOpenChange={open => { if (!open) {
      if (menuNavigation.current && keyboardNavigation.current) {
        if (!window.location.hash) main.current?.querySelector<HTMLElement>('[data-admin-page-heading]')?.focus({ preventScroll: true });
      } else opener.current?.focus({ preventScroll: true });
    } }}>{nav}</Drawer>
    <Modal rootClassName="admin-search-modal" title="Search Admin" afterClose={() => { if (!searchNavigation.current) searchOpener.current?.focus({ preventScroll: true }); }} open={searchOpen} footer={null} onCancel={() => setSearchOpen(false)} focusable={{ trap: true, focusTriggerAfterClose: false }} destroyOnHidden>
      <Flex vertical gap="var(--ac-space-default)">
        <Input autoFocus aria-label="Search Admin destinations" placeholder="Find a page or setting…" value={query} onChange={e => setQuery(e.target.value)} allowClear />
        <Typography.Text type="secondary" aria-live="polite">{searchItems.length} destinations · Navigation labels only</Typography.Text>
        <div className="admin-search-results">
          {!searchItems.length && <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No destinations found. Try model, voice, credentials or memory." />}
          {searchGroups.map(group => <section key={group.id} aria-label={`${group.label} destinations`} className="admin-search-group">
            <Typography.Title level={5} className="admin-search-group-heading">{group.label}</Typography.Title>
            {group.items.map(p => <a key={p.id} className="admin-search-result" href={p.href} onClick={e => { followAppLink(e); if (e.defaultPrevented) { searchNavigation.current = true; setSearchOpen(false); } }}><strong>{p.label}</strong><span>{p.description}</span></a>)}
          </section>)}
        </div>
      </Flex>
    </Modal>
  </Layout></AdminDraftStatus.Provider>;
}

export function AdminPageHeading({ page, metadata, extra }: { page?: NavigationPage; metadata?: ReactNode; extra?: ReactNode }) {
  return <div className="admin-page-heading">
    <Flex justify="space-between" align="center" wrap gap="var(--ac-space-default)">
      <Flex align="baseline" wrap gap="var(--ac-space-compact)"><Typography.Title level={2} tabIndex={-1} data-admin-page-heading>{page?.label ?? 'Page not found'}</Typography.Title>{metadata && <Typography.Text type="secondary">{metadata}</Typography.Text>}</Flex>
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
