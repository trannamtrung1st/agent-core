# Built-in General Assistant cutover

Ship the API, SPA, immutable `agents/general-assistant-v36.json` and migration `20261028030836_BuiltInGeneralAssistant` together. Existing v35 remains immutable for genuine historical/ordinary pins. The new-template catalogs propose only the current General Assistant release; no tenant-owned Definition copy is created.

1. Stop all writers/workers. Back up SQLite, protected keys and matching workspace/resource/attachment/artifact roots using the [persistence runbook](15-persistence-and-configuration.md). Verify that the matching backup can be restored.
2. Apply the additive migration to the intended database explicitly, using the Infrastructure design-time factory:

   ```sh
   dotnet ef database update --project src/AgentCore.Infrastructure --connection 'Data Source=/absolute/path/agent-core.db'
   ```

   It adds nullable `SystemIdentities.ManagedKind`, the filtered unique `(TenantId, ManagedKind)` index, `AgentInstances.ShowInChat=true`, and group-level `PersonaCustomized=false`. Existing ordinary Instances stay ordinary and visible; no installed user data is reset. The generated designer/snapshot own model metadata. The migration's native SQLite column removal on downgrade preserves existing cross-table retirement triggers. Restore a matching backup when rolling back an installed release.
3. Start the matching host. Bootstrap, both organization-creation application entry points and the live original-human system-organization endpoint coordinate the same trusted provisioning operation. Startup scans active tenants in pages of 100, with up to five CAS attempts per tenant. Creation atomically installs Instance, tenant executor identity, private Space/membership, narrow starter assignments, revision and system-origin audit. Repeated startup returns the same identity; it does not adopt an ordinary customer clone.
4. Check `system.general-assistant.provision` / `system.general-assistant.adopt` audits (system actor `00000000-0000-0000-0000-000000000000`) and the host's tenant-qualified setup failures. A failed tenant setup remains recoverable at next startup. Incompatible automatic adoption leaves configuration intact and blocks new managed admission with `assistant_upgrade_requires_attention`; correct the saved configuration or deliberately Reset to System Defaults and restart/reconcile. Completed data and admitted Run pins remain intact.
5. Sign in as an authorized member. The visible managed assistant is the first candidate in ordinary Chat. Synthetic uses the existing offline model catalog without credentials. Real needs an actually available tenant model/default; this feature never creates a provider credential, offering or external account. Voice starts off; optional Browser/Sandbox/external tools retain their separate setup, IAM and consent prerequisites.

Starter roles are narrowly bounded: conversation/model/Run/Attachment/Artifact rights in the owning tenant, read access to its own Instance/Skill/resource context, and read/write workspace plus Space-read access only to its exact private Space. No Owner, Contributor, agent-management, credential/Secret, sensitive Browser/Sandbox or cooperation assignment is installed. Shared Spaces require explicit membership and source IAM. Session context, human authority and operation review remain independently enforced.

`Visible in Chat` affects only new Chat discovery/search/counts. It does not stop Runs, change IAM, delete data or hide management/history. A stale browser selection is revalidated before Session creation and fails with an explicit unavailable message.

Reset uses one managed-only configuration transaction with current original-human authority, exact `agents.read` and `agents.update`, security/Instance witnesses and a fresh operation ID. It restores current persona/defaults and clears inherited configuration/enablement choices. It preserves visibility, identity, IAM, private Space, credentials/bindings, local Skills/resources, files, memory data and historical Session/Run/Task/approval evidence. Same-operation retry reads the existing audit receipt; mismatched operations and stale writes fail. After uncertain writes, inspect current settings before a fresh deliberate action; the UI never automatically replays them.

Local Synthetic evidence does not certify live providers, production load, physical audio/devices or deployment. See the delivery ledger for executed scenarios and limits.
