import type { AdminCapabilityDescriptor } from "../../services/adminApi";

export function groupedCapabilityOptions(catalog: AdminCapabilityDescriptor[], authorized?: string[]) {
  const rows = authorized ? catalog.filter(c => authorized.includes(c.name) && c.discoverable) : catalog;
  return [...new Set(rows.map(c => c.category))].sort().map(category => ({ label: category,
    options: rows.filter(c => c.category === category).map(c => ({ value: c.name,
      label: `${c.name}${c.configured ? "" : " · unavailable"}`, disabled: !!authorized && !c.configured })) }));
}
export function reconcileAlwaysCapabilities(names: string[], authorized: string[], catalog: AdminCapabilityDescriptor[]) {
  return names.filter(name => authorized.includes(name) && catalog.some(c => c.name === name && c.discoverable));
}
