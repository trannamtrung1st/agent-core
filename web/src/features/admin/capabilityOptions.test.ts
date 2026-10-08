import { describe, expect, it } from "vitest";
import { groupedCapabilityOptions, reconcileAlwaysCapabilities } from "./capabilityOptions";
import type { AdminCapabilityDescriptor } from "../../services/adminApi";
const catalog: AdminCapabilityDescriptor[] = [
  { name: "workspace.read", category: "Workspace", summary: "Read", tags: [], discoverable: true, configured: true, defaultProjectionClass: "onDemand" },
  { name: "browser.open", category: "Browser", summary: "Open", tags: [], discoverable: true, configured: false, defaultProjectionClass: "onDemand" },
  { name: "core.context", category: "Core", summary: "Context", tags: [], discoverable: false, configured: true, defaultProjectionClass: "contextOnly" }
];
describe("capability selection", () => {
  it("uses the same catalog categories and availability labels for both selectors", () => {
    const authorized = groupedCapabilityOptions(catalog);
    const always = groupedCapabilityOptions(catalog, catalog.map(c => c.name));
    expect(authorized.map(g => g.label)).toEqual(["Browser", "Core", "Workspace"]);
    expect(always.map(g => g.label)).toEqual(["Browser", "Workspace"]);
    expect(always[0].options[0]).toEqual({ value: "browser.open", label: "browser.open · unavailable", disabled: true });
    expect(always[1].options[0].label).toBe(authorized[2].options[0].label);
  });
  it("removes unauthorized, unknown and Core context-only names without granting anything", () => {
    expect(reconcileAlwaysCapabilities(["workspace.read", "browser.open", "core.context", "foreign.write"], ["workspace.read", "core.context"], catalog)).toEqual(["workspace.read"]);
    expect(groupedCapabilityOptions(catalog, ["workspace.read"])[0].options.map(o => o.value)).toEqual(["workspace.read"]);
  });
});
