import { AutomationTriggerPermissions } from "./AutomationTriggerPermissions";
import { orderedReasoningEfforts, retainedReasoningEffort } from "../models/reasoningEfforts";
import { DefinitionExecutionBudgets } from "./ExecutionBudgetsSection";
import { AgentConfigurationPanel, AgentIdentitySections } from "./AgentConfigurationLayout";
import { useEffect, useId, useState } from "react";
import { Alert, Button, Collapse, Flex, Input, InputNumber, Segmented, Select, Switch, Typography, theme } from "antd";
import { DeleteOutlined } from "@ant-design/icons";
import {
  applyVoiceEnabled,
  automationNumberErrors,
  memoryPolicyDefaults,
  patchRecord,
  readBoolean,
  readCandidateStringList,
  readMetadataRows,
  readNumber,
  readString,
  triggerPolicyDefaults,
  writeMetadataRows,
  writeModelDefault,
  writeNullableString,
  writePath,
  type DefinitionCandidate
} from "./definitionCandidate";
import { listAdminAuthoringOptions, type AdminAuthoringOptions } from "../../services/adminApi";
import { describeAdminError } from "./adminErrors";
import { DiagnosticDetails } from "../chat/DiagnosticDetails";

export type DefinitionEditorView = "form" | "json";

const INTERRUPTION_STYLES = [
  { value: "acknowledgeThenContinue", label: "Acknowledge, then continue" },
  { value: "answerNewTurn", label: "Answer the new turn" }
];

const RESPONSE_LENGTHS = [
  { value: "concise", label: "Concise" },
  { value: "balanced", label: "Balanced" }
];

const INITIATIVE_TRIGGERS = [
  { value: "longSilence", label: "Long silence" },
  { value: "environmentUpdate", label: "Environment update" },
  { value: "unfinishedInteraction", label: "Unfinished interaction" }
];


export function DefinitionCandidateEditor({
  candidate,
  view,
  jsonText,
  busy,
  readOnly = false,
  onCandidateChange,
  onJsonTextChange,
  onViewChange
}: {
  candidate: DefinitionCandidate;
  view: DefinitionEditorView;
  jsonText: string;
  busy: boolean;
  readOnly?: boolean;
  onCandidateChange: (candidate: DefinitionCandidate) => void;
  onJsonTextChange: (text: string) => void;
  onViewChange: (view: DefinitionEditorView) => void;
}) {
  const [authoring, setAuthoring] = useState<AdminAuthoringOptions | null>(null);
  const [authoringError, setAuthoringError] = useState<string | null>(null);
  const [authoringDiagnosticId, setAuthoringDiagnosticId] = useState<string | null>(null);
  useEffect(() => {
    let cancelled = false;
    void listAdminAuthoringOptions()
      .then((options) => {
        if (!cancelled) {
          setAuthoring(options);
          setAuthoringError(null);
          setAuthoringDiagnosticId(null);
        }
      })
      .catch((error: unknown) => {
        if (!cancelled) {
          setAuthoringError(readOnly
            ? "Authoring options could not be loaded. Inspect the stored values in Advanced JSON."
            : "Authoring options could not be loaded. Advanced JSON can still name a configured value.");
          setAuthoringDiagnosticId(describeAdminError(error, "").diagnosticId ?? null);
        }
      });
    return () => {
      cancelled = true;
    };
  }, [readOnly]);

  return (
    <Flex vertical gap={12}>
      <Segmented
        aria-label="Definition editor view"
        className="admin-draft-view-switch"
        value={view}
        options={[
          { label: "Form", value: "form" },
          { label: "Advanced JSON", value: "json" }
        ]}
        onChange={(value) => onViewChange(value as DefinitionEditorView)}
        disabled={busy}
      />
      {view === "json" ? (
        <AgentConfigurationPanel title="Advanced JSON" label="Definition JSON editor">
        <Input.TextArea
          aria-label="Advanced JSON"
          value={jsonText}
          rows={18}
          disabled={busy}
          readOnly={readOnly}
          spellCheck={false}
          className="admin-draft-instructions"
          onChange={(event) => onJsonTextChange(event.target.value)}
        />
        </AgentConfigurationPanel>
      ) : null}
      <div hidden={view !== "form"}>
        <DefinitionCandidateForm
          candidate={candidate}
          busy={busy}
          readOnly={readOnly}
          authoring={authoring}
          authoringError={authoringError}
          authoringDiagnosticId={authoringDiagnosticId}
          onCandidateChange={onCandidateChange}
        />
      </div>
    </Flex>
  );
}

function DefinitionCandidateForm({
  candidate,
  busy,
  readOnly,
  authoring,
  authoringError,
  authoringDiagnosticId,
  onCandidateChange
}: {
  candidate: DefinitionCandidate;
  busy: boolean;
  readOnly: boolean;
  authoring: AdminAuthoringOptions | null;
  authoringError: string | null;
  authoringDiagnosticId: string | null;
  onCandidateChange: (candidate: DefinitionCandidate) => void;
}) {
  const goals = readCandidateStringList(candidate, ["goals"]);
  const modelKey = readString(candidate, ["modelDefaults", "catalogKey"]);
  const reasoningEffort = readString(candidate, ["modelDefaults", "reasoningEffort"]);
  const languageModel = readString(candidate, ["providerPreferences", "languageModel"]);
  const speechRecognizer = readString(candidate, ["providerPreferences", "speechRecognizer"]);
  const speechSynthesizer = readString(candidate, ["providerPreferences", "speechSynthesizer"]);
  const interruptionClassifier = readString(candidate, ["providerPreferences", "interruptionClassifier"]);
  const voiceOn = readBoolean(candidate, ["voice", "enabled"]);
  const selectedModel = authoring?.models.find((item) => item.key === modelKey) ?? null;
  const reasoningChoices = orderedReasoningEfforts(selectedModel
    ? selectedModel.supportedReasoningEfforts
    : authoring?.models.flatMap((item) => item.supportedReasoningEfforts) ?? []);
  const speechMissing = voiceOn && (speechRecognizer.trim().length === 0 || speechSynthesizer.trim().length === 0);
  const goalRows = goals.length > 0 ? goals : [""];
  const metadataRows = readMetadataRows(candidate);
  const triggerErrors = automationNumberErrors(candidate);

  const setGoals = (next: string[]) => {
    onCandidateChange(writePath(candidate, ["goals"], next));
  };

  const { token } = theme.useToken();
  return (
    <AgentIdentitySections
      profile={
        <AgentConfigurationPanel title="Identity & goals" label="Identity and goals" className={`admin-definition-profile${readOnly ? " admin-draft-form-readonly" : ""}`}>
          <section className="admin-draft-form-section admin-settings-form">
            <div className="admin-draft-field">
              <Typography.Text strong>Definition ID</Typography.Text>
              <Typography.Text aria-label="Definition ID">{readString(candidate, ["definitionId"])}</Typography.Text>
            </div>
            <div className="admin-draft-field-grid admin-settings-field-grid">
              <TextField
                readOnly={readOnly}
                label="Definition name"
                value={readString(candidate, ["identity", "name"])}
                disabled={busy}
                onChange={(value) => onCandidateChange(patchRecord(candidate, ["identity"], { name: value }))}
              />
              <TextField
                readOnly={readOnly}
                label="Definition role"
                value={readString(candidate, ["identity", "role"])}
                disabled={busy}
                onChange={(value) => onCandidateChange(patchRecord(candidate, ["identity"], { role: value }))}
              />
            </div>
            <TextField
              readOnly={readOnly}
              multiline
              label="Definition description"
              value={readString(candidate, ["identity", "description"])}
              disabled={busy}
              onChange={(value) => onCandidateChange(patchRecord(candidate, ["identity"], { description: value }))}
            />
            <TextField
              readOnly={readOnly}
              label="Definition tone"
              value={readString(candidate, ["identity", "tone"])}
              disabled={busy}
              onChange={(value) => onCandidateChange(patchRecord(candidate, ["identity"], { tone: value }))}
            />
            <Flex vertical gap={8}>
              {goalRows.map((goal, index) => (
                <div key={`goal-${index}`} className="admin-draft-repeat-row">
                  <TextField
                    readOnly={readOnly}
                    label={`Goal ${index + 1}`}
                    value={goal}
                    disabled={busy}
                    onChange={(value) => {
                      const next = goalRows.map((item, itemIndex) => (itemIndex === index ? value : item));
                      setGoals(goals.length === 0 ? [value] : next);
                    }}
                  />
                  {!readOnly ? <Button
                    danger
                    type="text"
                    icon={<DeleteOutlined />}
                    aria-label={`Remove goal ${index + 1}`}
                    className="admin-knowledge-source-remove"
                    disabled={busy}
                    onClick={() => setGoals(goals.filter((_, itemIndex) => itemIndex !== index))}
                  /> : null}
                </div>
              ))}
            </Flex>
            {!readOnly ? <Button onClick={() => setGoals([...goals, ""])} disabled={busy}>
              Add goal
            </Button> : null}
          </section>
        </AgentConfigurationPanel>
      }
      settings={
        <Flex vertical gap={token.padding} className={`admin-draft-form-stack admin-definition-settings${readOnly ? " admin-draft-form-readonly" : ""}`}>
          <Typography.Paragraph type="secondary" style={{ margin: 0 }}>Defaults for this Definition. Instance settings inherit these values unless customized.</Typography.Paragraph>
          {authoringError ? (
            <Alert
              type="warning"
              showIcon
              title={authoringError}
              action={authoringDiagnosticId
                ? <DiagnosticDetails fields={{ diagnosticId: authoringDiagnosticId }} />
                : undefined}
            />
          ) : null}
          <Collapse items={[
            {
              key: "instructions", label: "Operating instructions", children: (
                <section className="admin-draft-form-section admin-settings-form admin-settings-instructions" aria-label="Instructions">
                  <label className="admin-draft-field">
                    <Typography.Text>System instructions</Typography.Text>
                    <Input.TextArea
                      aria-label="System instructions"
                      readOnly={readOnly}
                      rows={8}
                      value={readString(candidate, ["systemInstructions"])}
                      disabled={busy}
                      className="admin-draft-instructions"
                      onChange={(event) =>
                        onCandidateChange(writePath(candidate, ["systemInstructions"], event.target.value))
                      }
                    />
                  </label>
                </section>
              )
            },
            {
              key: "conversationPolicy", label: "Conversation", children: (
                <section className="admin-draft-form-section admin-settings-form" aria-label="Conversation">
                  <div className="admin-draft-field-grid admin-settings-field-grid">
                    <SelectField
                      label="Response length"
                      value={readString(candidate, ["conversationPolicy", "responseLength"])}
                      options={RESPONSE_LENGTHS}
                      disabled={busy || readOnly}
                      onChange={(value) =>
                        onCandidateChange(patchRecord(candidate, ["conversationPolicy"], { responseLength: value }))
                      }
                    />
                    <TextField
                      readOnly={readOnly}
                      label="Conversation language"
                      hint="auto, or a BCP 47 tag such as en."
                      value={readString(candidate, ["conversationPolicy", "language"])}
                      disabled={busy}
                      onChange={(value) =>
                        onCandidateChange(patchRecord(candidate, ["conversationPolicy"], { language: value }))
                      }
                    />
                    <NumberField
                      label="Max output tokens"
                      value={readNumber(candidate, ["conversationPolicy", "maxOutputTokens"])}
                      disabled={busy || readOnly}
                      onChange={(value) =>
                        onCandidateChange(
                          patchRecord(candidate, ["conversationPolicy"], {
                            maxOutputTokens: value
                          })
                        )
                      }
                    />
                    <SwitchField
                      label="Ask one question at a time"
                      checked={readBoolean(candidate, ["conversationPolicy", "askOneQuestionAtATime"])}
                      disabled={busy || readOnly}
                      onChange={(checked) =>
                        onCandidateChange(
                          patchRecord(candidate, ["conversationPolicy"], { askOneQuestionAtATime: checked })
                        )
                      }
                    />
                  </div>
                </section>
              )
            },
            {
              key: "behaviorPolicy", label: "Behavior", children: (
                <section className="admin-draft-form-section admin-settings-form" aria-label="Behavior">
                  <div className="admin-draft-field-grid admin-settings-field-grid">
                    <SelectField
                      label="Interruption style"
                      value={readString(candidate, ["behaviorPolicy", "interruptionStyle"])}
                      options={INTERRUPTION_STYLES}
                      disabled={busy || readOnly}
                      onChange={(value) =>
                        onCandidateChange(patchRecord(candidate, ["behaviorPolicy"], { interruptionStyle: value }))
                      }
                    />
                    <SwitchField
                      label="Acknowledge interruption"
                      checked={readBoolean(candidate, ["behaviorPolicy", "acknowledgeInterruption"])}
                      disabled={busy || readOnly}
                      onChange={(checked) =>
                        onCandidateChange(patchRecord(candidate, ["behaviorPolicy"], { acknowledgeInterruption: checked }))
                      }
                    />
                    <SwitchField
                      label="Avoid unsupported claims"
                      checked={readBoolean(candidate, ["behaviorPolicy", "avoidUnsupportedClaims"])}
                      disabled={busy || readOnly}
                      onChange={(checked) =>
                        onCandidateChange(patchRecord(candidate, ["behaviorPolicy"], { avoidUnsupportedClaims: checked }))
                      }
                    />
                  </div>
                </section>
              )
            },
            {
              key: "initiativePolicy", label: "Initiative", children: (
                <section className="admin-draft-form-section admin-settings-form" aria-label="Initiative">
                  <SwitchField
                    label="Initiative enabled"
                    checked={readBoolean(candidate, ["initiativePolicy", "enabled"])}
                    disabled={busy || readOnly}
                    onChange={(checked) =>
                      onCandidateChange(patchRecord(candidate, ["initiativePolicy"], { enabled: checked }))
                    }
                  />
                  <div className="admin-draft-field-grid admin-settings-field-grid">
                    <NumberField
                      label="Silence threshold"
                      hint="Milliseconds. 1,000 to 120,000."
                      value={readNumber(candidate, ["initiativePolicy", "silenceThresholdMs"])}
                      disabled={busy || readOnly}
                      onChange={(value) =>
                        onCandidateChange(patchRecord(candidate, ["initiativePolicy"], { silenceThresholdMs: value }))
                      }
                    />
                    <NumberField
                      label="Cooldown"
                      hint="Milliseconds. 5,000 to 600,000."
                      value={readNumber(candidate, ["initiativePolicy", "cooldownMs"])}
                      disabled={busy || readOnly}
                      onChange={(value) =>
                        onCandidateChange(patchRecord(candidate, ["initiativePolicy"], { cooldownMs: value }))
                      }
                    />
                    <NumberField
                      label="Max prompts per silence"
                      value={readNumber(candidate, ["initiativePolicy", "maxPerSilencePeriod"])}
                      disabled={busy || readOnly}
                      onChange={(value) =>
                        onCandidateChange(patchRecord(candidate, ["initiativePolicy"], { maxPerSilencePeriod: value }))
                      }
                    />
                    <NumberField
                      label="Max consecutive proactive turns"
                      hint="Empty uses 1."
                      value={readNumber(candidate, ["initiativePolicy", "maxConsecutiveProactiveTurns"])}
                      disabled={busy || readOnly}
                      onChange={(value) =>
                        onCandidateChange(
                          patchRecord(candidate, ["initiativePolicy"], { maxConsecutiveProactiveTurns: value })
                        )
                      }
                    />
                    <NumberField
                      label="Max silent evaluations"
                      hint="Empty uses 8."
                      value={readNumber(candidate, ["initiativePolicy", "maxSilentEvaluations"])}
                      disabled={busy || readOnly}
                      onChange={(value) =>
                        onCandidateChange(patchRecord(candidate, ["initiativePolicy"], { maxSilentEvaluations: value }))
                      }
                    />
                    <NumberField
                      label="Max inactivity"
                      hint="Milliseconds. Empty uses 900,000."
                      value={readNumber(candidate, ["initiativePolicy", "maxInactivityMs"])}
                      disabled={busy || readOnly}
                      onChange={(value) =>
                        onCandidateChange(patchRecord(candidate, ["initiativePolicy"], { maxInactivityMs: value }))
                      }
                    />
                  </div>
                  <SelectField
                    label="Initiative triggers"
                    mode="multiple"
                    value={readCandidateStringList(candidate, ["initiativePolicy", "triggers"])}
                    options={INITIATIVE_TRIGGERS}
                    disabled={busy || readOnly}
                    onChange={(value) =>
                      onCandidateChange(patchRecord(candidate, ["initiativePolicy"], { triggers: value }))
                    }
                  />
                </section>
              )
            },
            {
              key: "voice", label: "Voice", children: (
                <section className="admin-draft-form-section admin-settings-form" aria-label="Voice">
                  <SwitchField
                    label="Voice enabled"
                    checked={readBoolean(candidate, ["voice", "enabled"])}
                    disabled={busy || readOnly}
                    onChange={(checked) =>
                      onCandidateChange(applyVoiceEnabled(candidate, checked, {
                        speechRecognizer: authoring?.defaultSpeechRecognizerAlias ?? null,
                        speechSynthesizer: authoring?.defaultSpeechSynthesizerAlias ?? null
                      }))
                    }
                  />
                  <div className="admin-draft-field-grid admin-settings-field-grid">
                    <TextField
                      readOnly={readOnly}
                      label="Voice id"
                      value={readString(candidate, ["voice", "voiceId"])}
                      disabled={busy}
                      onChange={(value) => onCandidateChange(patchRecord(candidate, ["voice"], { voiceId: value }))}
                    />
                    <NumberField
                      label="Speaking rate"
                      hint="1 is normal speed. 0.5 to 2."
                      value={readNumber(candidate, ["voice", "speakingRate"])}
                      disabled={busy || readOnly}
                      step={0.1}
                      onChange={(value) => onCandidateChange(patchRecord(candidate, ["voice"], { speakingRate: value }))}
                    />
                  </div>
                </section>
              )
            },
            {
              key: "modelDefaults", label: "Model defaults", children: (
                <section className="admin-draft-form-section admin-settings-form" aria-label="Model defaults">
                  <div className="admin-draft-field-grid admin-settings-field-grid">
                    <SelectField
                      label="Model"
                      hint="Optional catalog model. Empty sets no model default."
                      allowClear
                      value={modelKey}
                      options={labeledOptions(
                        (authoring?.models ?? []).map((item) => ({ value: item.key, label: item.displayName })),
                        modelKey
                      )}
                      disabled={busy || readOnly}
                      onChange={(value) => {
                        const key = typeof value === "string" ? value : "";
                        let next = writeModelDefault(candidate, "catalogKey", key);
                        const model = authoring?.models.find((item) => item.key === key);
                        const allowed = model?.supportedReasoningEfforts ?? [];
                        const currentEffort = readString(next, ["modelDefaults", "reasoningEffort"]);
                        if (currentEffort.length > 0 && !allowed.includes(currentEffort)) {
                          next = writeModelDefault(next, "reasoningEffort", retainedReasoningEffort(model, currentEffort));
                        }
                        onCandidateChange(next);
                      }}
                    />
                    <SelectField
                      label="Reasoning"
                      hint={selectedModel && selectedModel.supportedReasoningEfforts.length === 0
                        ? "This model has no reasoning effort."
                        : "Optional effort supported by the selected model."}
                      allowClear
                      value={reasoningEffort}
                      options={labeledOptions(
                        reasoningChoices.map((item) => ({ value: item, label: effortLabel(item) })),
                        reasoningEffort
                      )}
                      disabled={busy || readOnly || (selectedModel !== null && selectedModel.supportedReasoningEfforts.length === 0)}
                      onChange={(value) =>
                        onCandidateChange(writeModelDefault(candidate, "reasoningEffort", typeof value === "string" ? value : ""))
                      }
                    />
                  </div>
                </section>
              )
            },
            {
              key: "memoryPolicy", label: "Memory policy", children: (
                <section className="admin-draft-form-section admin-settings-form" aria-label="Memory policy">
                  {(
                    [
                      ["sessionMemory", "Session memory"],
                      ["identityUserPromotion", "Identity user promotion"],
                      ["identityUserRetrieval", "Identity user retrieval"],
                      ["userPromotion", "User promotion"],
                      ["userRetrieval", "User retrieval"]
                    ] as const
                  ).map(([field, label]) => (
                    <SwitchField
                      key={field}
                      label={label}
                      checked={readBoolean(candidate, ["memoryPolicy", field])}
                      disabled={busy || readOnly}
                      onChange={(checked) =>
                        onCandidateChange(
                          patchRecord(candidate, ["memoryPolicy"], { [field]: checked }, memoryPolicyDefaults)
                        )
                      }
                    />
                  ))}
                </section>
              )
            },
            {
              key: "triggerPolicy", label: <Flex wrap align="center" gap="var(--ac-space-compact)">
                <span>Trigger restrictions</span>
                {triggerErrors.length > 0 && <Typography.Text type="danger" aria-hidden>Needs attention</Typography.Text>}
              </Flex>,
              children: (
                <section className="admin-draft-form-section admin-settings-form" aria-label="Automation policy">
                  {(
                    [
                      ["enabled", "Automation enabled"],
                      ["allowUserScheduling", "Allow user scheduling"],
                      ["allowOneShot", "Allow one-shot"],
                      ["allowDaily", "Allow daily"],
                      ["allowWeekly", "Allow weekly"],
                      ["allowIndefiniteRecurrence", "Allow indefinite recurrence"],
                      ["allowFixedInterval", "Allow fixed interval"]
                    ] as const
                  ).map(([field, label]) => (
                    <SwitchField
                      key={field}
                      label={label}
                      checked={readBoolean(candidate, ["triggerPolicy", field])}
                      disabled={busy || readOnly}
                      onChange={(checked) =>
                        onCandidateChange(
                          patchRecord(candidate, ["triggerPolicy"], { [field]: checked }, triggerPolicyDefaults)
                        )
                      }
                    />
                  ))}
                  <div className="admin-draft-field-grid admin-settings-field-grid">
                    <NumberField
                      label="Max active registrations"
                      value={readNumber(candidate, ["triggerPolicy", "maxActiveRegistrations"])}
                      hint="Whole registrations. 1 to 32."
                      error={triggerErrors.find(item => item.field === "maxActiveRegistrations")?.message}
                      disabled={busy || readOnly}
                      onChange={(value) =>
                        onCandidateChange(
                          patchRecord(
                            candidate,
                            ["triggerPolicy"],
                            { maxActiveRegistrations: value },
                            triggerPolicyDefaults
                          )
                        )
                      }
                    />
                    <NumberField
                      label="One-shot horizon days"
                      value={readNumber(candidate, ["triggerPolicy", "oneShotHorizonDays"])}
                      hint="Whole days. 1 to 365."
                      error={triggerErrors.find(item => item.field === "oneShotHorizonDays")?.message}
                      disabled={busy || readOnly}
                      onChange={(value) =>
                        onCandidateChange(
                          patchRecord(candidate, ["triggerPolicy"], { oneShotHorizonDays: value }, triggerPolicyDefaults)
                        )
                      }
                    />
                    <NumberField
                      label="Minimum recurrence days"
                      value={readNumber(candidate, ["triggerPolicy", "minRecurrenceDays"])}
                      hint="Whole days. 1 to 365."
                      error={triggerErrors.find(item => item.field === "minRecurrenceDays")?.message}
                      disabled={busy || readOnly}
                      onChange={(value) =>
                        onCandidateChange(
                          patchRecord(candidate, ["triggerPolicy"], { minRecurrenceDays: value }, triggerPolicyDefaults)
                        )
                      }
                    />
                    <NumberField
                      label="Minimum fixed interval seconds"
                      value={readNumber(candidate, ["triggerPolicy", "minFixedIntervalSeconds"])}
                      hint="Whole seconds. 60 to 604800."
                      error={triggerErrors.find(item => item.field === "minFixedIntervalSeconds")?.message}
                      disabled={busy || readOnly}
                      onChange={(value) =>
                        onCandidateChange(
                          patchRecord(
                            candidate,
                            ["triggerPolicy"],
                            { minFixedIntervalSeconds: value },
                            triggerPolicyDefaults
                          )
                        )
                      }
                    />
                  </div>
                  <AutomationTriggerPermissions
                    values={readCandidateStringList(candidate, ["triggerPolicy", "allowedSourceKinds"])}
                    enabled={readBoolean(candidate, ["triggerPolicy", "enabled"])}
                    disabled={busy || readOnly}
                    onChange={allowedSourceKinds => onCandidateChange(patchRecord(candidate, ["triggerPolicy"], { allowedSourceKinds }, triggerPolicyDefaults))}
                  />
                </section>
              )
            },
            {
              key: "providerPreferences", label: "Provider preferences", children: (
                <section className="admin-draft-form-section admin-settings-form" aria-label="Provider preferences">
                  <Typography.Text type="secondary" className="admin-draft-field-hint">
                    Language-model provider is required. Speech aliases are required when voice is on and must stay empty when voice is off. If this server has one speech recognizer and one synthesizer, turning voice on selects them. Otherwise, select both. Advanced JSON does not gain aliases on save.
                  </Typography.Text>
                  <div className="admin-draft-field-grid admin-settings-field-grid">
                    <SelectField
                      label="Language-model provider"
                      value={languageModel}
                      options={labeledOptions(
                        (authoring?.languageModelAliases ?? []).map((item) => ({ value: item, label: item })),
                        languageModel
                      )}
                      disabled={busy || readOnly}
                      onChange={(value) =>
                        onCandidateChange(patchRecord(candidate, ["providerPreferences"], {
                          languageModel: typeof value === "string" ? value : ""
                        }))
                      }
                    />
                    <SelectField
                      label="Speech recognizer"
                      allowClear
                      value={speechRecognizer}
                      options={labeledOptions(
                        (authoring?.speechRecognizerAliases ?? []).map((item) => ({ value: item, label: item })),
                        speechRecognizer
                      )}
                      disabled={busy || readOnly || !voiceOn}
                      onChange={(value) =>
                        onCandidateChange(writeNullableString(
                          candidate,
                          ["providerPreferences", "speechRecognizer"],
                          typeof value === "string" ? value : ""
                        ))
                      }
                    />
                    <SelectField
                      label="Speech synthesizer"
                      allowClear
                      value={speechSynthesizer}
                      options={labeledOptions(
                        (authoring?.speechSynthesizerAliases ?? []).map((item) => ({ value: item, label: item })),
                        speechSynthesizer
                      )}
                      disabled={busy || readOnly || !voiceOn}
                      onChange={(value) =>
                        onCandidateChange(writeNullableString(
                          candidate,
                          ["providerPreferences", "speechSynthesizer"],
                          typeof value === "string" ? value : ""
                        ))
                      }
                    />
                    <SelectField
                      label="Interruption classifier"
                      value={interruptionClassifier}
                      options={labeledOptions(
                        (authoring?.interruptionClassifiers ?? []).map((item) => ({ value: item, label: item })),
                        interruptionClassifier
                      )}
                      disabled={busy || readOnly}
                      onChange={(value) =>
                        onCandidateChange(patchRecord(candidate, ["providerPreferences"], {
                          interruptionClassifier: typeof value === "string" ? value : ""
                        }))
                      }
                    />
                  </div>
                  {speechMissing ? (
                    <Alert type="info" showIcon title="Select a speech recognizer and synthesizer." />
                  ) : null}
                </section>
              )
            },
            {
              key: "metadata", label: "Advanced / metadata", children: (
                <section className="admin-draft-form-section admin-settings-form" aria-label="Advanced metadata">
                  <div className="admin-draft-field">
                    <Typography.Text strong>Schema version</Typography.Text>
                    <Typography.Text aria-label="Schema version">
                      {readNumber(candidate, ["schemaVersion"]) ?? readString(candidate, ["schemaVersion"])}
                    </Typography.Text>
                  </div>
                  <Flex vertical gap={8}>
                    {metadataRows.map((row, index) => (
                      <div key={`metadata-${index}`} className="admin-draft-metadata-row">
                        <TextField
                          readOnly={readOnly}
                          label={`Metadata key ${index + 1}`}
                          value={row.key}
                          disabled={busy}
                          onChange={(value) => {
                            const next = metadataRows.map((item, itemIndex) =>
                              itemIndex === index ? { ...item, key: value } : item
                            );
                            onCandidateChange(writeMetadataRows(candidate, next));
                          }}
                        />
                        <TextField
                          readOnly={readOnly}
                          label={`Metadata value ${index + 1}`}
                          value={row.value}
                          disabled={busy}
                          onChange={(value) => {
                            const next = metadataRows.map((item, itemIndex) =>
                              itemIndex === index ? { ...item, value } : item
                            );
                            onCandidateChange(writeMetadataRows(candidate, next));
                          }}
                        />
                        {!readOnly ? <Button
                          danger
                          type="text"
                          icon={<DeleteOutlined />}
                          aria-label={`Remove metadata ${index + 1}`}
                          className="admin-knowledge-source-remove"
                          disabled={busy}
                          onClick={() =>
                            onCandidateChange(writeMetadataRows(
                              candidate,
                              metadataRows.filter((_, itemIndex) => itemIndex !== index)
                            ))
                          }
                        /> : null}
                      </div>
                    ))}
                  </Flex>
                  {!readOnly ? <Button
                    onClick={() => onCandidateChange(writeMetadataRows(candidate, [...metadataRows, { key: "", value: "" }]))}
                    disabled={busy || metadataRows.some((row) => row.key === "")}
                  >
                    Add metadata
                  </Button> : null}
                </section>
              )
            },
          ]} />
          <DefinitionExecutionBudgets candidate={candidate} busy={busy} readOnly={readOnly} onChange={onCandidateChange} />
        </Flex>
      }
    />
  );
}

function TextField({
  label,
  hint,
  value,
  disabled,
  readOnly = false,
  multiline = false,
  onChange
}: {
  label: string;
  hint?: string;
  value: string;
  disabled: boolean;
  readOnly?: boolean;
  multiline?: boolean;
  onChange: (value: string) => void;
}) {
  const hintId = useId();
  return (
    <label className="admin-draft-field">
      <Typography.Text>{label}</Typography.Text>
      {readOnly || multiline ? <Input.TextArea
        aria-label={label}
        aria-describedby={hint ? hintId : undefined}
        value={value}
        disabled={disabled}
        readOnly={readOnly}
        rows={multiline ? 3 : undefined}
        autoSize={readOnly}
        onChange={(event) => onChange(event.target.value)}
      /> : <Input
        aria-label={label}
        aria-describedby={hint ? hintId : undefined}
        value={value}
        disabled={disabled}
        onChange={(event) => onChange(event.target.value)}
      />}
      {hint ? (
        <Typography.Text id={hintId} type="secondary" className="admin-draft-field-hint">
          {hint}
        </Typography.Text>
      ) : null}
    </label>
  );
}

function NumberField({
  label,
  hint,
  error,
  value,
  disabled,
  step,
  onChange
}: {
  label: string;
  hint?: string;
  error?: string;
  value: number | null;
  disabled: boolean;
  step?: number;
  onChange: (value: number | null) => void;
}) {
  const hintId = useId();
  const errorId = useId();
  return (
    <label className="admin-draft-field">
      <Typography.Text>{label}</Typography.Text>
      <InputNumber
        aria-label={label}
        aria-describedby={[hint ? hintId : null, error ? errorId : null].filter(Boolean).join(" ") || undefined}
        aria-invalid={error ? true : undefined}
        status={error ? "error" : undefined}
        value={value}
        disabled={disabled}
        step={step}
        onChange={(next) => onChange(typeof next === "number" ? next : null)}
      />
      {hint ? (
        <Typography.Text id={hintId} type="secondary" className="admin-draft-field-hint">
          {hint}
        </Typography.Text>
      ) : null}
      {error ? <Typography.Text id={errorId} type="danger" role="alert">{error}</Typography.Text> : null}
    </label>
  );
}

function effortLabel(value: string) {
  return value.length === 0 ? value : `${value.charAt(0).toUpperCase()}${value.slice(1)}`;
}

function labeledOptions(
  options: Array<{ value: string; label: string }>,
  current: string
) {
  if (current.trim().length === 0 || options.some((item) => item.value === current)) {
    return options;
  }
  return [...options, { value: current, label: current }];
}

function SelectField({
  label,
  value,
  options,
  disabled,
  mode,
  allowClear,
  hint,
  onChange
}: {
  label: string;
  value: string | string[];
  options: Array<{ value: string; label: string }>;
  disabled: boolean;
  mode?: "multiple";
  allowClear?: boolean;
  hint?: string;
  onChange: (value: string | string[]) => void;
}) {
  const hintId = useId();
  const empty = mode === "multiple" ? (value as string[]).length === 0 : value === "";
  return (
    <label className="admin-draft-field">
      <Typography.Text>{label}</Typography.Text>
      <Select
        aria-label={label}
        aria-describedby={hint ? hintId : undefined}
        mode={mode}
        allowClear={allowClear}
        value={empty ? undefined : value}
        options={options}
        disabled={disabled}
        onChange={(next) => {
          if (mode === "multiple") {
            onChange(Array.isArray(next) ? next : []);
            return;
          }
          onChange(typeof next === "string" ? next : "");
        }}
      />
      {hint ? (
        <Typography.Text id={hintId} type="secondary" className="admin-draft-field-hint">
          {hint}
        </Typography.Text>
      ) : null}
    </label>
  );
}

function SwitchField({
  label,
  checked,
  disabled,
  onChange
}: {
  label: string;
  checked: boolean;
  disabled: boolean;
  onChange: (checked: boolean) => void;
}) {
  const id = useId();
  const { token } = theme.useToken();
  return (
    <Flex align="center" gap={token.paddingSM} className="admin-settings-switch-row">
      <Typography.Text><label htmlFor={id}>{label}</label></Typography.Text>
      <Switch id={id} aria-label={label} checked={checked} disabled={disabled} onChange={onChange} />
    </Flex>
  );
}
