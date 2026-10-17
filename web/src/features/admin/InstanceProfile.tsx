import { InstanceVersionChanges } from './InstanceVersionChanges';
import { InstancePersonaFields } from './InstancePersonaFields';
import { useInstanceConfigurationEditor } from './InstanceConfigurationActions';
import { Alert, App, Button, Flex, Select, Tag, Typography } from "antd";
import { useEffect, useState } from "react";
import { confirmAction } from "../../app/confirmAction";
import { deleteAdminAgentInstance, listAdminDefinitionPublications, listAdminDefinitions, updateAdminAgentInstanceActiveVersion, updateAdminAgentInstanceLifecycle, updateAdminAgentInstancePersona, type AdminDefinitionInventoryItem, type AdminDefinitionPublicationSummary, type AdminEffectiveConfiguration } from "../../services/adminApi";
import { previewInstanceVersion } from "../../services/instanceConfiguration";
import { DiagnosticDetails } from "../chat/DiagnosticDetails";
import { AdminDeletionBlockedAlert } from "./adminDeletionBlocked";
import { describeAdminError, type AdminFailureNotice } from "./adminErrors";
import { showAdminFailure } from "./adminFailure";
import { AgentConfigurationPanel } from "./AgentConfigurationLayout";
import { buildInstanceVersionOptions, isPersonaDraftDirty, managedInstanceVersionActionLabel, parsePersonaJson, syncPersonaOnTabChange, type PersonaFields } from "./instanceManagedControls";

export function InstanceManagedControls({
  config,
  onUpdated,
  onDeleted,
  onDirtyChange,
  page
}: {
  page?: 'profile' | 'version' | 'lifecycle';
  config: AdminEffectiveConfiguration;
  onUpdated: () => void;
  onDeleted: () => void;
  onDirtyChange?: (dirty: boolean) => void;
}) {
  const { message, modal } = App.useApp();
  const [busy, setBusy] = useState(false);
  const [deleteError, setDeleteError] = useState<AdminFailureNotice | null>(null);
  const [personaTab, setPersonaTab] = useState("form");
  const [persona, setPersona] = useState<PersonaFields>(() => ({ ...config.persona }));
  const [personaJsonDraft, setPersonaJsonDraft] = useState(() =>
    JSON.stringify(config.persona, null, 2)
  );
  const [personaJsonError, setPersonaJsonError] = useState<string | null>(null);
  const [definitionInventory, setDefinitionInventory] = useState<AdminDefinitionInventoryItem[]>([]);
  const [inventoryError, setInventoryError] = useState<AdminFailureNotice | null>(null);
  const [publications, setPublications] = useState<AdminDefinitionPublicationSummary[]>([]);
  const [publicationsError, setPublicationsError] = useState<AdminFailureNotice | null>(null);
  const [targetVersion, setTargetVersion] = useState(config.definitionVersion);

  useEffect(() => {
    setPersona({ ...config.persona });
    setPersonaJsonDraft(JSON.stringify(config.persona, null, 2));
    setPersonaJsonError(null);
  }, [config.instanceId, config.personaRevision]);

  useEffect(() => {
    setTargetVersion(config.definitionVersion);
  }, [config.instanceId, config.definitionVersion]);

  useEffect(() => {
    setDeleteError(null);
  }, [config.instanceId, config.instanceLifecycle]);

  useEffect(() => {
    setInventoryError(null);
    void listAdminDefinitions()
      .then((items) => setDefinitionInventory(items))
      .catch((error) => {
        setDefinitionInventory([]);
        setInventoryError(describeAdminError(error, "Definition inventory unavailable."));
      });
  }, []);

  useEffect(() => {
    setPublicationsError(null);
    void listAdminDefinitionPublications(config.definitionId)
      .then((items) => setPublications(items))
      .catch((error) => {
        setPublications([]);
        setPublicationsError(describeAdminError(error, "Publications unavailable."));
      });
  }, [config.definitionId]);

  const handlePersonaTabChange = (nextTab: string) => {
    const synced = syncPersonaOnTabChange(personaTab, nextTab, persona, personaJsonDraft);
    if (!synced.ok) {
      setPersonaJsonError(synced.error);
      message.error(synced.error);
      return;
    }
    setPersona(synced.persona);
    setPersonaJsonDraft(synced.personaJsonDraft);
    setPersonaJsonError(null);
    setPersonaTab(nextTab);
  };

  const resolvePersonaForSave = (): PersonaFields | null => {
    if (personaTab === "form") {
      return persona;
    }
    try {
      const parsed = parsePersonaJson(personaJsonDraft);
      setPersona(parsed);
      setPersonaJsonError(null);
      return parsed;
    } catch (error) {
      const text = error instanceof Error ? error.message : "Invalid persona JSON.";
      setPersonaJsonError(text);
      message.error(text);
      return null;
    }
  };

  const handleMutationError = (error: unknown) => {
    const notice = showAdminFailure(message, error, "Update failed.");
    if (notice.message.includes("reload")) {
      onUpdated();
    }
  };

  const savePersona = async (expectedRevision?: number) => {
    const nextPersona = resolvePersonaForSave();
    if (!nextPersona) {
      if (expectedRevision !== undefined) throw new Error("Repair Persona JSON before saving.");
      return;
    }
    setBusy(true);
    try {
      const updated = await updateAdminAgentInstancePersona(config.instanceId, {
        expectedRevision: expectedRevision ?? config.instanceRevision,
        expectedPersonaRevision: config.personaRevision,
        ...nextPersona
      });
      message.success("Persona updated.");
      if (expectedRevision === undefined) onUpdated();
      return updated?.revision;
    } catch (error) {
      if (expectedRevision !== undefined) throw error;
      handleMutationError(error);
    } finally {
      setBusy(false);
    }
  };

  const setLifecycle = async (lifecycle: "Active" | "Archived") => {
    setBusy(true);
    try {
      await updateAdminAgentInstanceLifecycle(config.instanceId, config.instanceRevision, lifecycle);
      message.success(lifecycle === "Archived" ? "Instance archived." : "Instance unarchived.");
      onUpdated();
    } catch (error) {
      handleMutationError(error);
    } finally {
      setBusy(false);
    }
  };

  const deleteInstance = async () => {
    setBusy(true);
    setDeleteError(null);
    try {
      await deleteAdminAgentInstance(config.instanceId, config.instanceRevision);
      message.success("Instance deleted.");
      onDeleted();
    } catch (error) {
      setDeleteError(describeAdminError(error, "Instance could not be deleted."));
    } finally {
      setBusy(false);
    }
  };

  const applyVersion = async () => {
    if (targetVersion === config.definitionVersion) {
      return;
    }
    setBusy(true);
    try {
      const ownerId = config.instanceId; const version = targetVersion;
      const preview = await previewInstanceVersion(ownerId, version);
      if (preview.instanceRevision !== config.instanceRevision) throw new Error("Instance changed. Reload and preview again.");
      confirmAction(modal, {
        title: `Adopt Definition v${version}?`,
        content: <InstanceVersionChanges preview={preview} />, okText: 'Adopt version', onOk: async () => {
          await updateAdminAgentInstanceActiveVersion(ownerId, preview.instanceRevision, version);
          message.success(`Instance adopted v${version}.`); onUpdated();
        }
      });
    } catch (error) {
      handleMutationError(error);
    } finally {
      setBusy(false);
    }
  };

  const versionOptions = buildInstanceVersionOptions(
    config.definitionId,
    config.definitionVersion,
    definitionInventory,
    publications
  );
  const versionActionLabel = managedInstanceVersionActionLabel(targetVersion, config.definitionVersion);

  const personaDirty = isPersonaDraftDirty(
    config.persona,
    personaTab,
    persona,
    personaJsonDraft
  );

  useEffect(() => { onDirtyChange?.(personaDirty); }, [personaDirty, onDirtyChange]);
  let invalidPersonaJson: string | undefined;
  if (personaTab === 'json') { try { parsePersonaJson(personaJsonDraft); } catch { invalidPersonaJson = 'Repair Persona JSON before saving.'; } }
  const configuration = useInstanceConfigurationEditor('Profile', { dirty: personaDirty, busy, revision: config.instanceRevision, blocked: invalidPersonaJson,
    save: async revision => { const updated = await savePersona(revision); if (updated === undefined) throw new Error('Persona revision could not be confirmed.'); return updated; },
    discard: () => { setPersona({ ...config.persona }); setPersonaJsonDraft(JSON.stringify(config.persona, null, 2)); setPersonaJsonError(null); } });
  return (
    <AgentConfigurationPanel title="Instance management" label="Managed instance controls"
      description="Update the persona, active definition version, and lifecycle."
      className="admin-instance-management" bodyClassName={`admin-instance-control-grid${page ? " admin-instance-focused" : ""}`}>
        <section hidden={!!page && page !== "profile"} className="admin-instance-persona" aria-label="Persona editor">
          <Flex align="baseline" gap="var(--ac-space-compact)" wrap>
            <Typography.Title level={5}>Persona</Typography.Title>
            <Typography.Text type="secondary">
              Rev {config.personaRevision}
            </Typography.Text>
          </Flex>
          <InstancePersonaFields persona={persona} onChange={setPersona} tab={personaTab} onTabChange={handlePersonaTabChange}
            json={personaJsonDraft} onJsonChange={value => { setPersonaJsonDraft(value); setPersonaJsonError(null); }}
            error={personaJsonError || invalidPersonaJson} disabled={busy || config.instanceLifecycle !== "Active"} />
          <Flex vertical gap={8} align="start" className="admin-instance-persona-actions">
            {!configuration.managed && <Button type="primary" onClick={() => void savePersona()} disabled={busy || config.instanceLifecycle !== "Active"}>
              Save persona
            </Button>}
            {personaDirty ? (
              <Typography.Text type="secondary">
                Unsaved persona edits stay in this editor across lifecycle and version changes until you save or
                leave this instance.
              </Typography.Text>
            ) : null}
          </Flex>
        </section>
        <div hidden={page === "profile"} className="admin-instance-assignment">
          <section hidden={page === "lifecycle"} aria-label="Active version">
            <Typography.Title level={5}>Active version</Typography.Title>
            <Typography.Paragraph type="secondary">
              Choose the immutable Definition version used by the next new Run, including in existing conversations.
            </Typography.Paragraph>
            {inventoryError ? (
              <Alert
                type="warning"
                showIcon
                title={inventoryError.message}
                action={inventoryError.diagnosticId
                  ? <DiagnosticDetails fields={{ diagnosticId: inventoryError.diagnosticId }} />
                  : undefined}
              />
            ) : null}
            {publicationsError ? (
              <Alert
                type="warning"
                showIcon
                title={publicationsError.message}
                action={publicationsError.diagnosticId
                  ? <DiagnosticDetails fields={{ diagnosticId: publicationsError.diagnosticId }} />
                  : undefined}
              />
            ) : null}
            <Flex gap={8} wrap="wrap" align="center" className="admin-instance-version-actions">
              <Select
                aria-label="Target definition version"
                value={targetVersion}
                options={versionOptions}
                onChange={setTargetVersion}
                disabled={busy || config.instanceLifecycle !== "Active"}
              />
              <Button onClick={() => void applyVersion()}
                disabled={busy || targetVersion === config.definitionVersion}>
                {versionActionLabel}
              </Button>
            </Flex>
          </section>
          <section hidden={page === "version"} className="admin-instance-lifecycle" aria-label="Lifecycle controls">
            <Flex align="baseline" gap="var(--ac-space-compact)" wrap>
              <Typography.Title level={5}>Lifecycle</Typography.Title>
              <Tag color={config.instanceLifecycle === "Active" ? "green" : "default"}>
                {config.instanceLifecycle}
              </Tag>
            </Flex>
            <Typography.Paragraph type="secondary">
              Archived instances keep history but cannot start new chats or triggered work. Delete is available after archive, and only when nothing still references this instance.
            </Typography.Paragraph>
            {deleteError ? (
              <AdminDeletionBlockedAlert
                className="admin-destructive-detail"
                message={deleteError.message}
                diagnosticId={deleteError.diagnosticId}
              />
            ) : null}
            <Flex gap={8} wrap="wrap">
              {config.instanceLifecycle === "Active" ? (
                <Button
                  danger
                  disabled={busy}
                  onClick={() =>
                    confirmAction(modal, {
                      title: "Archive this managed instance?",
                      content: "New chats and triggered work will stop until you unarchive. Unsaved persona edits stay in this editor.",
                      okText: "Archive",
                      danger: true,
                      onOk: () => void setLifecycle("Archived")
                    })
                  }
                >
                  Archive instance
                </Button>
              ) : (
                <Button disabled={busy} onClick={() => void setLifecycle("Active")}>
                  Unarchive instance
                </Button>
              )}
              {config.instanceLifecycle === "Archived" ? (
                <Button
                  danger
                  disabled={busy}
                  onClick={() =>
                    confirmAction(modal, {
                      title: "Delete this archived instance?",
                      content:
                        "This permanently removes the managed instance. Sessions, learned memory, triggers, and background work stay in place. If any of those still reference this instance, deletion is refused.",
                      okText: "Delete instance",
                      cancelText: "Keep instance",
                      danger: true,
                      onOk: () => void deleteInstance()
                    })
                  }
                >
                  Delete instance
                </Button>
              ) : null}
            </Flex>
          </section>
          <Typography.Text type="secondary" className="admin-instance-revision">
            Instance revision {config.instanceRevision}
          </Typography.Text>
        </div>
    </AgentConfigurationPanel>
  );
}
