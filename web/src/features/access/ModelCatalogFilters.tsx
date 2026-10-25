import { Col, Grid, Row, Select, theme } from 'antd';
import { capabilityFilters, kindLabel, modelKinds, type ModelConnection, type ModelKind } from '../../services/models';

export type ModelCatalogFilterValue = { provider?: ModelKind; connectionId?: string; state?: string; capability?: string };

/** Shared model collection filters; native Selects own keyboard navigation and clear behavior. */
export function ModelCatalogFilters({ value, connections, states, stateLabel, onChange }: {
  value: ModelCatalogFilterValue; connections?: ModelConnection[]; states: { value: string; label: string }[];
  stateLabel: string; onChange: (value: ModelCatalogFilterValue) => void;
}) {
  const { token } = theme.useToken();
  const screens = Grid.useBreakpoint();
  const size = screens.md ? 'middle' : 'large';
  const columns = connections ? { xs: 24, sm: 12, xl: 6 } : { xs: 24, sm: 8 };
  return <Row gutter={[token.paddingXS, token.paddingXS]}>
    <Col {...columns}><Select<ModelKind | ''> size={size} aria-label="Filter model provider" placeholder="All providers" value={value.provider ?? ''}
      options={[{ value: '', label: 'All providers' }, ...modelKinds.map(kind => ({ value: kind, label: kindLabel(kind) }))]} style={{ width: '100%' }} onChange={provider => onChange({ ...value, provider: provider || undefined })} /></Col>
    {connections && <Col {...columns}><Select size={size} aria-label="Filter model connection" showSearch optionFilterProp="label" placeholder="All connections" value={value.connectionId ?? ''}
      options={[{ value: '', label: 'All connections' }, ...connections.map(c => ({ value: c.connectionId, label: c.label }))]} style={{ width: '100%' }} onChange={connectionId => onChange({ ...value, connectionId: connectionId || undefined })} /></Col>}
    <Col {...columns}><Select size={size} aria-label={stateLabel} placeholder="All states" value={value.state ?? ''} options={[{ value: '', label: 'All states' }, ...states]}
      style={{ width: '100%' }} onChange={state => onChange({ ...value, state: state || undefined })} /></Col>
    <Col {...columns}><Select size={size} aria-label="Filter model capability" placeholder="All capabilities" value={value.capability ?? ''}
      options={[{ value: '', label: 'All capabilities' }, ...capabilityFilters.map(item => ({ value: item.value, label: item.text }))]} style={{ width: '100%' }} onChange={capability => onChange({ ...value, capability: capability || undefined })} /></Col>
  </Row>;
}
