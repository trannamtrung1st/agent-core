import { Alert, App, Button, Flex, Result } from "antd";
import { useCallback, useEffect, useRef, useState } from "react";
import { followAppLink } from "../../app/appLink";
import { adminDefinitionPath, adminHomePath, adminInstancePath, navigateToAppPath, type AdminRoute } from "../../app/appRoute";
import { getAdminEffectiveConfig, listAdminDefinitions, listAdminInstances, type AdminDefinitionInventoryItem, type AdminInstanceInventoryItem } from "../../services/adminApi";
import { formatAdminLoadError } from "./adminErrors";
import { AdminShell } from './AdminShell';
import { AgentConfigurationPanel } from "./AgentConfigurationLayout";
import { BrowserPrivacySection } from "./BrowserPrivacySection";
import { CredentialsSection } from "./CredentialsSection";
import { EventsSection } from "./EventsSection";
import { ExecutionBudgetLimitsProvider } from "./executionBudgetLimits";
import { RetainedAdminPage } from './RetainedAdminPage';

import { InventorySection, NewDefinitionButton, NewInstanceButton } from './AdminGlobalPages';
import { formatAdminTimestamp, formatDefinitionInventoryCounts, formatDefinitionVersionSummary, formatInventoryStatus, groupDefinitionInventory, latestActiveVersion, type EffectiveConfigLoadState, type LoadState } from './adminPresentation';
import { DefinitionDetail } from './DefinitionDetail';
import { InstanceDetail } from './InstanceDetail';
export function AdminApp({ route }: { route: AdminRoute }) {
  return <ExecutionBudgetLimitsProvider><AdminAppContent route={route} /></ExecutionBudgetLimitsProvider>;
}
function AdminAppContent({ route }: { route: AdminRoute }) {
  const collection = route.view === "home" ? route.collection ?? "instances" : undefined;
  const [definitions, setDefinitions] = useState<LoadState<AdminDefinitionInventoryItem[]>>({ kind: "loading" });
  const [instances, setInstances] = useState<LoadState<AdminInstanceInventoryItem[]>>({ kind: "loading" });
  const [effectiveConfig, setEffectiveConfig] = useState<EffectiveConfigLoadState>({ kind: "loading" });
  const effectiveRequest = useRef(0);
  const effectiveOwner = useRef<string | undefined>(undefined);
  effectiveOwner.current = route.view === "instance" ? route.instanceId : undefined;

  const reloadDefinitions = useCallback(async () => {
    setDefinitions({ kind: "loading" });
    try {
      const definitionItems = await listAdminDefinitions();
      setDefinitions({ kind: "ready", data: definitionItems });
    } catch (error) {
      const formatted = formatAdminLoadError(error);
      setDefinitions({
        kind: "error",
        message: formatted.message,
        unauthorized: formatted.unauthorized,
        diagnosticId: formatted.diagnosticId
      });
    }
  }, []);

  const reloadInstances = useCallback(async () => {
    setInstances({ kind: "loading" });
    try {
      const instanceItems = await listAdminInstances();
      setInstances({ kind: "ready", data: instanceItems });
    } catch (error) {
      const formatted = formatAdminLoadError(error);
      setInstances({
        kind: "error",
        message: formatted.message,
        unauthorized: formatted.unauthorized,
        diagnosticId: formatted.diagnosticId
      });
    }
  }, []);

  const reloadEffectiveConfig = useCallback(async (instanceId: string) => {
    if (effectiveOwner.current !== instanceId) return;
    const request = ++effectiveRequest.current;
    setEffectiveConfig(current => ({ kind: "loading",
      data: current.data?.instanceId === instanceId ? current.data : undefined }));
    try {
      const data = await getAdminEffectiveConfig(instanceId);
      if (request === effectiveRequest.current && effectiveOwner.current === instanceId) setEffectiveConfig({ kind: "ready", data });
    } catch (error) {
      if (request !== effectiveRequest.current) return;
      const formatted = formatAdminLoadError(error);
      setEffectiveConfig(current => ({
        kind: "error",
        message: formatted.message,
        unauthorized: formatted.unauthorized,
        diagnosticId: formatted.diagnosticId,
        data: formatted.unauthorized ? undefined : current.data
      }));
    }
  }, []);

  useEffect(() => {
    if (route.view === "definition" || route.view === "draft" || collection === "definitions" || collection === "instances") void reloadDefinitions();
    if (route.view === "instance" || collection === "instances") void reloadInstances();
  }, [route.view, collection, reloadDefinitions, reloadInstances]);

  const routedInstanceId = route.view === "instance" ? route.instanceId : undefined;
  useEffect(() => {
    if (!routedInstanceId) {
      ++effectiveRequest.current;
      setEffectiveConfig({ kind: "loading" });
      return;
    }

    void reloadEffectiveConfig(routedInstanceId);
    return () => { ++effectiveRequest.current; };
  }, [routedInstanceId, reloadEffectiveConfig]);

  const definitionGroups = definitions.kind === "ready"
    ? groupDefinitionInventory(definitions.data)
    : [];

  return (
    <App className="antd-root" message={{ duration: 3, maxCount: 3 }}>
    <AdminShell route={route} ownerName={route.view === "instance" && effectiveConfig.data?.instanceId === route.instanceId ? effectiveConfig.data.persona.name : undefined}>
        {route.view === "home" ? (
          <div className="admin-home">
            <Flex gap={8} wrap className="admin-home-intro">
              {collection === "definitions" ? <NewDefinitionButton groups={definitionGroups} /> : null}
              {collection === "instances" ? <NewInstanceButton loading={definitions.kind !== "ready"} groups={definitionGroups.filter(group => group.versions.length > 0)} /> : null}
            </Flex>
            {collection === "instances" && definitions.kind === "error" && <Alert type="error" showIcon title={`Definition choices unavailable. ${definitions.message}`} action={<Button onClick={() => void reloadDefinitions()}>Retry Definition choices</Button>} />}
            <RetainedAdminPage active={collection === "definitions"}><InventorySection
                title="Definitions"
                countLabel={definitions.kind === "ready"
                  ? `${definitionGroups.length} ${definitionGroups.length === 1 ? "definition" : "definitions"}`
                  : null}
                emptyLabel="No definitions found."
                loading={definitions.kind === "loading"}
                error={definitions.kind === "error" ? definitions.message : null}
                diagnosticId={definitions.kind === "error" ? definitions.diagnosticId : null}
                unauthorized={definitions.kind === "error" ? (definitions.unauthorized ?? false) : false}
                onRetry={() => void reloadDefinitions()}
                items={
                  definitions.kind === "ready"
                    ? definitionGroups.map((group) => ({
                        key: group.definitionId,
                        title: group.logicalName,
                        secondary: group.definitionId,
                        latestActive: latestActiveVersion(group.versions),
                        draftCount: group.draftCount,
                        versionCount: group.versions.length,
                        description: formatDefinitionVersionSummary(group),
                        detail: formatDefinitionInventoryCounts(group),
                        tag: group.draftOnly ? "Draft" : formatInventoryStatus(group.latestStatus),
                        version: group.latestVersion,
                        href: adminDefinitionPath(group.definitionId)
                      }))
                    : []
                }
              /></RetainedAdminPage>
            <RetainedAdminPage active={collection === "instances"}><InventorySection
                title="Instances"
                countLabel={instances.kind === "ready"
                  ? `${instances.data.length} ${instances.data.length === 1 ? "instance" : "instances"}`
                  : null}
                emptyLabel="No instances yet."
                loading={instances.kind === "loading"}
                error={instances.kind === "error" ? instances.message : null}
                diagnosticId={instances.kind === "error" ? instances.diagnosticId : null}
                unauthorized={instances.kind === "error" ? (instances.unauthorized ?? false) : false}
                onRetry={() => void reloadInstances()}
                items={
                  instances.kind === "ready"
                    ? instances.data.map((item) => ({
                        key: item.instanceId,
                        title: `${item.personaName} · ${item.definitionId}`,
                        name: item.personaName,
                        definitionId: item.definitionId,
                        secondary: item.instanceId,
                        description: `Pinned to v${item.activeVersion}`,
                        version: item.activeVersion,
                        tag: "Agent Instance",
                        status: item.lifecycle,
                        detail: `Updated ${formatAdminTimestamp(item.updatedAt)}`,
                        updatedAt: item.updatedAt,
                        href: adminInstancePath(item.instanceId)
                      }))
                    : []
                }
              /></RetainedAdminPage>
            <RetainedAdminPage active={collection === "credentials"}><CredentialsSection /></RetainedAdminPage>
            <RetainedAdminPage active={collection === "events"}><EventsSection /></RetainedAdminPage>
            <RetainedAdminPage active={collection === "browser-privacy"}><AgentConfigurationPanel title="Host Browser privacy" label="Global Browser privacy" bodyGap="section"><BrowserPrivacySection /></AgentConfigurationPanel></RetainedAdminPage>
          </div>
        ) : null}

        {route.view === "definition" || route.view === "draft" ? (
          <DefinitionDetail
            key={route.definitionId}
            definitionId={route.definitionId}
            tab={route.view === "draft" ? "drafts" : route.tab}
            draftId={route.view === "draft" ? route.draftId : undefined}
            draftPage={route.view === "draft" ? route.page : undefined}
            definitions={definitions}
            onBack={() => navigateToAppPath(adminHomePath("definitions"))}
            onRetryDefinitions={reloadDefinitions}
            onDeleted={() => {
              void reloadDefinitions();
              navigateToAppPath(adminHomePath("definitions"));
            }}
          />
        ) : null}

        {route.view === "instance" ? (
          <InstanceDetail
            key={route.instanceId}
            instanceId={route.instanceId}
            page={route.page}
            instances={instances}
            effective={effectiveConfig}
            onBack={() => navigateToAppPath(adminHomePath("instances"))}
            onRetryEffective={() => void reloadEffectiveConfig(route.instanceId)}
            onInstanceChanged={async () => {
              await Promise.all([reloadEffectiveConfig(route.instanceId), reloadInstances()]);
            }}
            onInstanceDeleted={() => {
              void reloadInstances();
              navigateToAppPath(adminHomePath("instances"));
            }}
          />
        ) : null}
        {route.view === "not-found" && <Result status="404" title="Admin page not found" subTitle="Check the address or return to the Instance inventory." extra={<Button href={adminHomePath()} onClick={followAppLink}>Back to Instances</Button>} />}
    </AdminShell>
    </App>
  );
}

export { defaultForkSourceVersion,formatDefinitionInventoryCounts,formatDefinitionVersionSummary,formatForkSourceOptionLabel,groupDefinitionInventory,latestActiveVersion } from './adminPresentation';
export { PublicationResourcesSummary } from './AdminRecordSummaries';
export { EffectiveConfigView } from './EffectiveConfigView';
export { InstanceDetail } from './InstanceDetail';
export { InstanceManagedControls } from './InstanceProfile';
