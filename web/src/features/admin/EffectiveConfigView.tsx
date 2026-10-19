import { Descriptions, Flex, Typography, theme } from "antd";
import { followAppLink } from "../../app/appLink";
import { systemBrowserPrivacyPath } from "../../services/systemBrowserPrivacy";
import { type AdminEffectiveConfiguration } from "../../services/adminApi";
import { InstanceIdentityTags } from './AdminRecordSummaries';
import { automationTriggerPermissionSummary } from "./AutomationTriggerPermissions";
import { useAdminDetailLayout } from "./useAdminDetailLayout";
import { InstanceSandboxSummary } from './InstanceSandboxSummary';

export function EffectiveConfigView({
  config,
  hidePersona = false
}: {
  config: AdminEffectiveConfiguration;
  hidePersona?: boolean;
}) {
  const trigger = config.triggerPolicy;
  const detailLayout = useAdminDetailLayout();
  const { token } = theme.useToken();
  return (
    <div className="admin-effective-config-grid">
      <InstanceSandboxSummary instanceId={config.instanceId} revision={config.instanceRevision} />
      <section
        className={`admin-effective-config-section admin-instance-identity${hidePersona ? " admin-effective-config-wide" : ""}`}
        aria-label="Instance identity"
      >
        <Typography.Title level={5}>Instance identity</Typography.Title>
        <InstanceIdentityTags
                    lifecycle={config.instanceLifecycle}
          definitionStatus={config.definitionStatus}
        />
        <Descriptions {...detailLayout} bordered size="small" column={1}>
          <Descriptions.Item label="Instance id">{config.instanceId}</Descriptions.Item>
          <Descriptions.Item label="Definition status">{config.definitionStatus}</Descriptions.Item>
          <Descriptions.Item label="Lifecycle">{config.instanceLifecycle}</Descriptions.Item>
        </Descriptions>
      </section>
      {hidePersona ? null : (
        <section className="admin-effective-config-section" aria-label="Persona">
          <Typography.Title level={5}>Persona</Typography.Title>
          <Descriptions {...detailLayout} bordered size="small" column={1}>
            <Descriptions.Item label="Name">{config.persona.name}</Descriptions.Item>
            <Descriptions.Item label="Role">{config.persona.role}</Descriptions.Item>
            <Descriptions.Item label="Description">{config.persona.description}</Descriptions.Item>
            <Descriptions.Item label="Tone">{config.persona.tone}</Descriptions.Item>
          </Descriptions>
        </section>
      )}
      <section className="admin-effective-config-section admin-effective-config-wide" aria-label="Runtime model">
        <Typography.Title level={5}>Runtime model</Typography.Title>
        <Descriptions {...detailLayout} bordered size="small" column={1}>
          <Descriptions.Item label="Definition">
            {config.definitionId} v{config.definitionVersion} ({config.definitionSource})
          </Descriptions.Item>
          <Descriptions.Item label="Display name">{config.effectiveModel.displayName}</Descriptions.Item>
          <Descriptions.Item label="Catalog key">{config.effectiveModel.catalogKey}</Descriptions.Item>
          <Descriptions.Item label="Model id">{config.effectiveModel.modelId ?? "None"}</Descriptions.Item>
          <Descriptions.Item label="Selection source">{config.effectiveModel.selectionSource}</Descriptions.Item>
          <Descriptions.Item label="Reasoning effort">{config.effectiveModel.reasoningEffort ?? "None"}</Descriptions.Item>
          <Descriptions.Item label="Language model alias">{config.providerPreferences.languageModel}</Descriptions.Item>
          <Descriptions.Item label="Speech recognizer">
            {config.providerPreferences.speechRecognizer ?? "None"}
          </Descriptions.Item>
          <Descriptions.Item label="Speech synthesizer">
            {config.providerPreferences.speechSynthesizer ?? "None"}
          </Descriptions.Item>
          <Descriptions.Item label="Interruption classifier">
            {config.providerPreferences.interruptionClassifier}
          </Descriptions.Item>
        </Descriptions>
      </section>
      <section
        className="admin-effective-config-section admin-effective-config-wide"
        aria-label="Tools and resources"
      >
        <Typography.Title level={5}>Tools and resources</Typography.Title>
        <Descriptions {...detailLayout} bordered size="small" column={1}>
          <Descriptions.Item label="Offered tools">
            {config.effectiveToolAllowlist.length > 0 ? config.effectiveToolAllowlist.join(", ") : "None"}
          </Descriptions.Item>
          <Descriptions.Item label="Harness references">
            {config.harnessReferences.length > 0 ? config.harnessReferences.join(", ") : "None"}
          </Descriptions.Item>
          <Descriptions.Item label="Workspace template">{config.workspaceTemplateId ?? "None"}</Descriptions.Item>
          <Descriptions.Item label="Knowledge">
            {config.knowledgeSources.length > 0
              ? config.knowledgeSources
                  .map((item) =>
                    item.resolvedResourcePath
                      ? `${item.identity} (${item.title}) · ${item.resolvedResourcePath}`
                      : `${item.identity} (${item.title})`
                  )
                  .join("; ")
              : "None"}
          </Descriptions.Item>
        </Descriptions>
      </section>
      {config.browser ? (
        <section className="admin-effective-config-section admin-effective-config-wide" aria-label="Browser provider">
          <Typography.Title level={5}>Browser provider</Typography.Title>
          <Flex vertical gap={token.padding}>
          <Descriptions {...detailLayout} bordered size="small" column={1}>
            <Descriptions.Item label="Provider">{config.browser.displayName} ({config.browser.providerId})</Descriptions.Item>
            <Descriptions.Item label="Engine">{config.browser.engine || "Unknown"}</Descriptions.Item>
            <Descriptions.Item label="Readiness">{!config.browser.enabled ? "Disabled" : config.browser.ready ? "Ready" : "Unavailable"}</Descriptions.Item>
            <Descriptions.Item label="Profile mode">{config.browser.profileMode}</Descriptions.Item>
            <Descriptions.Item label="Policy mode">{config.browser.policyMode}</Descriptions.Item>
            <Descriptions.Item label="Supported features">{config.browser.supportedFeatures.join(", ") || "None"}</Descriptions.Item>
            <Descriptions.Item label="Output limits">Snapshot {config.browser.maxSnapshotBytes} UTF-8 bytes; screenshot {config.browser.maxCaptureBytes} bytes; download {config.browser.maxDownloadBytes} bytes · per request</Descriptions.Item>
            {config.browser.limits ? <>
              <Descriptions.Item label="Operation budgets">Deadline {config.browser.limits.operationTimeoutMs} ms; explicit wait {config.browser.limits.waitTimeoutMs} ms; automatic settle {config.browser.limits.automaticSettleMs} ms</Descriptions.Item>
              <Descriptions.Item label="Capture allowance">{config.browser.limits.capturesPerScope} {config.browser.limits.capturesPerScope === 1 ? "screenshot" : "screenshots"} and {config.browser.limits.downloadsPerScope} {config.browser.limits.downloadsPerScope === 1 ? "download" : "downloads"} per Run or occurrence; Session fallback</Descriptions.Item>
              <Descriptions.Item label="Interaction limits">{config.browser.limits.findMatches} matches per find; {config.browser.limits.textInputLength} characters per input</Descriptions.Item>
              <Descriptions.Item label="Configuration source">Host Browser:Limits configuration · changes apply after host restart. Safety ceilings remain enforced.</Descriptions.Item>
            </> : null}
          </Descriptions>
          <a href={systemBrowserPrivacyPath} onClick={followAppLink}>Manage global Browser privacy · Host restart rules apply</a>
          </Flex>
        </section>
      ) : null}
      <section className="admin-effective-config-section" aria-label="Memory policy">
        <Typography.Title level={5}>Memory policy</Typography.Title>
        <Descriptions {...detailLayout} bordered size="small" column={1}>
          <Descriptions.Item label="Session memory">{config.memoryPolicy.sessionMemory ? "On" : "Off"}</Descriptions.Item>
          <Descriptions.Item label="Identity promotion">
            {config.memoryPolicy.identityUserPromotion ? "On" : "Off"}
          </Descriptions.Item>
          <Descriptions.Item label="Identity retrieval">
            {config.memoryPolicy.identityUserRetrieval ? "On" : "Off"}
          </Descriptions.Item>
          <Descriptions.Item label="User promotion">{config.memoryPolicy.userPromotion ? "On" : "Off"}</Descriptions.Item>
          <Descriptions.Item label="User retrieval">{config.memoryPolicy.userRetrieval ? "On" : "Off"}</Descriptions.Item>
        </Descriptions>
      </section>
      <section className="admin-effective-config-section" aria-label="Automation">
        <Typography.Title level={5}>Automation</Typography.Title>
        <Descriptions {...detailLayout} bordered size="small" column={1}>
          <Descriptions.Item label="Trigger enabled">{trigger?.enabled ? "Yes" : "No"}</Descriptions.Item>
          <Descriptions.Item label="User scheduling">{trigger?.allowUserScheduling ? "Yes" : "No"}</Descriptions.Item>
          <Descriptions.Item label="Automation trigger permissions">
            {automationTriggerPermissionSummary(trigger?.allowedSourceKinds)}
            {!trigger?.enabled && <Typography.Paragraph type="secondary">Configured permissions are inactive while Automation is disabled.</Typography.Paragraph>}
          </Descriptions.Item>
          <Descriptions.Item label="One-shot / daily / weekly">
            {trigger
              ? `${trigger.allowOneShot ? "one-shot" : "—"} / ${trigger.allowDaily ? "daily" : "—"} / ${trigger.allowWeekly ? "weekly" : "—"}`
              : "None"}
          </Descriptions.Item>
          <Descriptions.Item label="Recurrence limits">
            {trigger
              ? `max registrations ${trigger.maxActiveRegistrations}, horizon ${trigger.oneShotHorizonDays}d, min recurrence ${trigger.minRecurrenceDays}d`
              : "None"}
          </Descriptions.Item>
          <Descriptions.Item label="Fixed interval">
            {trigger?.allowFixedInterval
              ? `allowed (min ${trigger.minFixedIntervalSeconds}s)`
              : trigger
                ? "disabled"
                : "None"}
          </Descriptions.Item>
          <Descriptions.Item label="Durable work eligibility">
            {config.durableExecutionEligibility.canAcceptNewTriggeredWork ? "Eligible" : "Not eligible"} · schedule{" "}
            {config.durableExecutionEligibility.allowsScheduleSource ? "allowed" : "blocked"} · application events{" "}
            {config.durableExecutionEligibility.allowsApplicationEventSource ? "allowed" : "blocked"}
          </Descriptions.Item>
        </Descriptions>
      </section>
    </div>
  );
}
