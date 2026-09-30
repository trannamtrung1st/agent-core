import { useEffect, useId, useState } from "react";
import { Alert, Button, Flex, Input, InputNumber, Segmented, Select, Switch, Typography } from "antd";
import { DeleteOutlined } from "@ant-design/icons";
import {
  applyVoiceEnabled,
  memoryPolicyDefaults,
  patchRecord,
  readBoolean,
  readCandidateStringList,
  readMetadataRows,
  readNumber,
  readSkills,
  readString,
  triggerPolicyDefaults,
  writeMetadataRows,
  writeModelDefault,
  writeNullableString,
  writePath,
  writeSkills,
  type DefinitionCandidate,
  type SkillDraft
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

const TRIGGER_SOURCE_KINDS = [
  { value: "schedule", label: "Schedule" },
  { value: "applicationEvent", label: "Application event" }
];

export function DefinitionCandidateEditor({
  candidate,
  view,
  jsonText,
  busy,
  onCandidateChange,
  onJsonTextChange,
  onViewChange
}: {
  candidate: DefinitionCandidate;
  view: DefinitionEditorView;
  jsonText: string;
  busy: boolean;
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
          setAuthoringError("Authoring options could not be loaded. Advanced JSON can still name a configured value.");
          setAuthoringDiagnosticId(describeAdminError(error, "").diagnosticId ?? null);
        }
      });
    return () => {
      cancelled = true;
    };
  }, []);

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
        <Input.TextArea
          aria-label="Advanced JSON"
          value={jsonText}
          rows={18}
          disabled={busy}
          spellCheck={false}
          className="admin-draft-instructions"
          onChange={(event) => onJsonTextChange(event.target.value)}
        />
      ) : (
        <DefinitionCandidateForm
          candidate={candidate}
          busy={busy}
          authoring={authoring}
          authoringError={authoringError}
          authoringDiagnosticId={authoringDiagnosticId}
          onCandidateChange={onCandidateChange}
        />
      )}
    </Flex>
  );
}

function DefinitionCandidateForm({
  candidate,
  busy,
  authoring,
  authoringError,
  authoringDiagnosticId,
  onCandidateChange
}: {
  candidate: DefinitionCandidate;
  busy: boolean;
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
  const reasoningChoices = selectedModel
    ? selectedModel.supportedReasoningEfforts
    : [...new Set(authoring?.models.flatMap((item) => item.supportedReasoningEfforts) ?? [])];
  const speechMissing = voiceOn && (speechRecognizer.trim().length === 0 || speechSynthesizer.trim().length === 0);
  const goalRows = goals.length > 0 ? goals : [""];
  const metadataRows = readMetadataRows(candidate);

  const setGoals = (next: string[]) => {
    onCandidateChange(writePath(candidate, ["goals"], next));
  };

  return (
    <Flex vertical gap={16} className="admin-draft-form-stack">
      <section className="admin-draft-form-section" aria-label="Identity and goals">
        <Typography.Title level={5}>Identity &amp; goals</Typography.Title>
        <div className="admin-draft-field">
          <Typography.Text strong>Definition ID</Typography.Text>
          <Typography.Text aria-label="Definition ID">{readString(candidate, ["definitionId"])}</Typography.Text>
        </div>
        <div className="admin-draft-field-grid">
          <TextField
            label="Definition name"
            value={readString(candidate, ["identity", "name"])}
            disabled={busy}
            onChange={(value) => onCandidateChange(patchRecord(candidate, ["identity"], { name: value }))}
          />
          <TextField
            label="Definition role"
            value={readString(candidate, ["identity", "role"])}
            disabled={busy}
            onChange={(value) => onCandidateChange(patchRecord(candidate, ["identity"], { role: value }))}
          />
          <TextField
            label="Definition tone"
            value={readString(candidate, ["identity", "tone"])}
            disabled={busy}
            onChange={(value) => onCandidateChange(patchRecord(candidate, ["identity"], { tone: value }))}
          />
        </div>
        <TextField
          label="Definition description"
          value={readString(candidate, ["identity", "description"])}
          disabled={busy}
          onChange={(value) => onCandidateChange(patchRecord(candidate, ["identity"], { description: value }))}
        />
        <Flex vertical gap={8}>
          {goalRows.map((goal, index) => (
            <div key={`goal-${index}`} className="admin-draft-repeat-row">
              <TextField
                label={`Goal ${index + 1}`}
                value={goal}
                disabled={busy}
                onChange={(value) => {
                  const next = goalRows.map((item, itemIndex) => (itemIndex === index ? value : item));
                  setGoals(goals.length === 0 ? [value] : next);
                }}
              />
              <Button
                danger
                type="text"
                icon={<DeleteOutlined />}
                aria-label={`Remove goal ${index + 1}`}
                className="admin-knowledge-source-remove"
                disabled={busy}
                onClick={() => setGoals(goals.filter((_, itemIndex) => itemIndex !== index))}
              />
            </div>
          ))}
        </Flex>
        <Button onClick={() => setGoals([...goals, ""])} disabled={busy}>
          Add goal
        </Button>
      </section>

      <section className="admin-draft-form-section" aria-label="Instructions">
        <Typography.Title level={5}>Instructions</Typography.Title>
        <label className="admin-draft-field">
          <Typography.Text strong>System instructions</Typography.Text>
          <Input.TextArea
            aria-label="System instructions"
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

      <section className="admin-draft-form-section" aria-label="Skills">
        <Typography.Title level={5}>Skills</Typography.Title>
        <Typography.Paragraph type="secondary">
          Skills are procedures for this definition. Required capabilities are requirements, not grants. They do not add tools, credentials, or approval.
        </Typography.Paragraph>
        {readSkills(candidate).length === 0 ? (
          <Typography.Text type="secondary">No skills yet. A definition without skills runs with an empty skill set.</Typography.Text>
        ) : (
          readSkills(candidate).map((skill, index) => (
            <SkillCard
              key={`skill-${index}`}
              index={index}
              skill={skill}
              disabled={busy}
              onChange={(next) => {
                const skills = readSkills(candidate).map((item, itemIndex) => (itemIndex === index ? next : item));
                onCandidateChange(writeSkills(candidate, skills));
              }}
              onRemove={() => {
                const existing = Array.isArray(candidate.skills) ? candidate.skills : [];
                const nextExisting = existing.filter((_, itemIndex) => itemIndex !== index);
                const skills = readSkills(candidate).filter((_, itemIndex) => itemIndex !== index);
                onCandidateChange(writeSkills({ ...candidate, skills: nextExisting }, skills));
              }}
            />
          ))
        )}
        <Button
          onClick={() => onCandidateChange(writeSkills(candidate, [...readSkills(candidate), emptySkill()]))}
          disabled={busy}
        >
          Add skill
        </Button>
      </section>

      <section className="admin-draft-form-section" aria-label="Behavior and conversation">
        <Typography.Title level={5}>Behavior &amp; conversation</Typography.Title>
        <div className="admin-draft-field-grid">
          <SelectField
            label="Interruption style"
            value={readString(candidate, ["behaviorPolicy", "interruptionStyle"])}
            options={INTERRUPTION_STYLES}
            disabled={busy}
            onChange={(value) =>
              onCandidateChange(patchRecord(candidate, ["behaviorPolicy"], { interruptionStyle: value }))
            }
          />
          <SelectField
            label="Response length"
            value={readString(candidate, ["conversationPolicy", "responseLength"])}
            options={RESPONSE_LENGTHS}
            disabled={busy}
            onChange={(value) =>
              onCandidateChange(patchRecord(candidate, ["conversationPolicy"], { responseLength: value }))
            }
          />
          <TextField
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
            disabled={busy}
            onChange={(value) =>
              onCandidateChange(
                patchRecord(candidate, ["conversationPolicy"], {
                  maxOutputTokens: value
                })
              )
            }
          />
        </div>
        <SwitchField
          label="Acknowledge interruption"
          checked={readBoolean(candidate, ["behaviorPolicy", "acknowledgeInterruption"])}
          disabled={busy}
          onChange={(checked) =>
            onCandidateChange(patchRecord(candidate, ["behaviorPolicy"], { acknowledgeInterruption: checked }))
          }
        />
        <SwitchField
          label="Avoid unsupported claims"
          checked={readBoolean(candidate, ["behaviorPolicy", "avoidUnsupportedClaims"])}
          disabled={busy}
          onChange={(checked) =>
            onCandidateChange(patchRecord(candidate, ["behaviorPolicy"], { avoidUnsupportedClaims: checked }))
          }
        />
        <SwitchField
          label="Ask one question at a time"
          checked={readBoolean(candidate, ["conversationPolicy", "askOneQuestionAtATime"])}
          disabled={busy}
          onChange={(checked) =>
            onCandidateChange(
              patchRecord(candidate, ["conversationPolicy"], { askOneQuestionAtATime: checked })
            )
          }
        />
      </section>

      <section className="admin-draft-form-section" aria-label="Initiative">
        <Typography.Title level={5}>Initiative</Typography.Title>
        <SwitchField
          label="Initiative enabled"
          checked={readBoolean(candidate, ["initiativePolicy", "enabled"])}
          disabled={busy}
          onChange={(checked) =>
            onCandidateChange(patchRecord(candidate, ["initiativePolicy"], { enabled: checked }))
          }
        />
        <div className="admin-draft-field-grid">
          <NumberField
            label="Silence threshold"
            hint="Milliseconds. 1,000 to 120,000."
            value={readNumber(candidate, ["initiativePolicy", "silenceThresholdMs"])}
            disabled={busy}
            onChange={(value) =>
              onCandidateChange(patchRecord(candidate, ["initiativePolicy"], { silenceThresholdMs: value }))
            }
          />
          <NumberField
            label="Cooldown"
            hint="Milliseconds. 5,000 to 600,000."
            value={readNumber(candidate, ["initiativePolicy", "cooldownMs"])}
            disabled={busy}
            onChange={(value) =>
              onCandidateChange(patchRecord(candidate, ["initiativePolicy"], { cooldownMs: value }))
            }
          />
          <NumberField
            label="Max prompts per silence"
            value={readNumber(candidate, ["initiativePolicy", "maxPerSilencePeriod"])}
            disabled={busy}
            onChange={(value) =>
              onCandidateChange(patchRecord(candidate, ["initiativePolicy"], { maxPerSilencePeriod: value }))
            }
          />
          <NumberField
            label="Max consecutive proactive turns"
            hint="Empty uses 1."
            value={readNumber(candidate, ["initiativePolicy", "maxConsecutiveProactiveTurns"])}
            disabled={busy}
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
            disabled={busy}
            onChange={(value) =>
              onCandidateChange(patchRecord(candidate, ["initiativePolicy"], { maxSilentEvaluations: value }))
            }
          />
          <NumberField
            label="Max inactivity"
            hint="Milliseconds. Empty uses 900,000."
            value={readNumber(candidate, ["initiativePolicy", "maxInactivityMs"])}
            disabled={busy}
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
          disabled={busy}
          onChange={(value) =>
            onCandidateChange(patchRecord(candidate, ["initiativePolicy"], { triggers: value }))
          }
        />
      </section>

      <section className="admin-draft-form-section" aria-label="Model and providers">
        <Typography.Title level={5}>Model &amp; providers</Typography.Title>
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
        <Typography.Text strong>Model defaults</Typography.Text>
        <div className="admin-draft-field-grid">
          <SelectField
            label="Model"
            hint="Optional catalog model. Empty sets no model default."
            allowClear
            value={modelKey}
            options={labeledOptions(
              (authoring?.models ?? []).map((item) => ({ value: item.key, label: item.displayName })),
              modelKey
            )}
            disabled={busy}
            onChange={(value) => {
              const key = typeof value === "string" ? value : "";
              let next = writeModelDefault(candidate, "catalogKey", key);
              const model = authoring?.models.find((item) => item.key === key);
              const allowed = model?.supportedReasoningEfforts ?? [];
              const currentEffort = readString(next, ["modelDefaults", "reasoningEffort"]);
              if (currentEffort.length > 0 && !allowed.includes(currentEffort)) {
                next = writeModelDefault(next, "reasoningEffort", "");
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
            disabled={busy || (selectedModel !== null && selectedModel.supportedReasoningEfforts.length === 0)}
            onChange={(value) =>
              onCandidateChange(writeModelDefault(candidate, "reasoningEffort", typeof value === "string" ? value : ""))
            }
          />
        </div>
        <Typography.Text strong>Provider preferences</Typography.Text>
        <Typography.Text type="secondary" className="admin-draft-field-hint">
          Language-model provider is required. Speech aliases are required when voice is on and must stay empty when voice is off. If this server has one speech recognizer and one synthesizer, turning voice on selects them. Otherwise, select both. Advanced JSON does not gain aliases on save.
        </Typography.Text>
        <div className="admin-draft-field-grid">
          <SelectField
            label="Language-model provider"
            value={languageModel}
            options={labeledOptions(
              (authoring?.languageModelAliases ?? []).map((item) => ({ value: item, label: item })),
              languageModel
            )}
            disabled={busy}
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
            disabled={busy || !voiceOn}
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
            disabled={busy || !voiceOn}
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
            disabled={busy}
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

      <section className="admin-draft-form-section" aria-label="Voice">
        <Typography.Title level={5}>Voice</Typography.Title>
        <SwitchField
          label="Voice enabled"
          checked={readBoolean(candidate, ["voice", "enabled"])}
          disabled={busy}
          onChange={(checked) =>
            onCandidateChange(applyVoiceEnabled(candidate, checked, {
              speechRecognizer: authoring?.defaultSpeechRecognizerAlias ?? null,
              speechSynthesizer: authoring?.defaultSpeechSynthesizerAlias ?? null
            }))
          }
        />
        <div className="admin-draft-field-grid">
          <TextField
            label="Voice id"
            value={readString(candidate, ["voice", "voiceId"])}
            disabled={busy}
            onChange={(value) => onCandidateChange(patchRecord(candidate, ["voice"], { voiceId: value }))}
          />
          <NumberField
            label="Speaking rate"
            hint="1 is normal speed. 0.5 to 2."
            value={readNumber(candidate, ["voice", "speakingRate"])}
            disabled={busy}
            step={0.1}
            onChange={(value) => onCandidateChange(patchRecord(candidate, ["voice"], { speakingRate: value }))}
          />
        </div>
      </section>

      <section className="admin-draft-form-section" aria-label="Memory policy">
        <Typography.Title level={5}>Memory policy</Typography.Title>
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
            disabled={busy}
            onChange={(checked) =>
              onCandidateChange(
                patchRecord(candidate, ["memoryPolicy"], { [field]: checked }, memoryPolicyDefaults)
              )
            }
          />
        ))}
      </section>

      <section className="admin-draft-form-section" aria-label="Automation policy">
        <Typography.Title level={5}>Automation policy</Typography.Title>
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
            disabled={busy}
            onChange={(checked) =>
              onCandidateChange(
                patchRecord(candidate, ["triggerPolicy"], { [field]: checked }, triggerPolicyDefaults)
              )
            }
          />
        ))}
        <div className="admin-draft-field-grid">
          <NumberField
            label="Max active registrations"
            value={readNumber(candidate, ["triggerPolicy", "maxActiveRegistrations"])}
            disabled={busy}
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
            disabled={busy}
            onChange={(value) =>
              onCandidateChange(
                patchRecord(candidate, ["triggerPolicy"], { oneShotHorizonDays: value }, triggerPolicyDefaults)
              )
            }
          />
          <NumberField
            label="Minimum recurrence days"
            value={readNumber(candidate, ["triggerPolicy", "minRecurrenceDays"])}
            disabled={busy}
            onChange={(value) =>
              onCandidateChange(
                patchRecord(candidate, ["triggerPolicy"], { minRecurrenceDays: value }, triggerPolicyDefaults)
              )
            }
          />
          <NumberField
            label="Minimum fixed interval seconds"
            value={readNumber(candidate, ["triggerPolicy", "minFixedIntervalSeconds"])}
            disabled={busy}
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
        <SelectField
          label="Allowed source kinds"
          mode="multiple"
          value={readCandidateStringList(candidate, ["triggerPolicy", "allowedSourceKinds"])}
          options={TRIGGER_SOURCE_KINDS}
          disabled={busy}
          onChange={(value) =>
            onCandidateChange(
              patchRecord(candidate, ["triggerPolicy"], { allowedSourceKinds: value }, triggerPolicyDefaults)
            )
          }
        />
      </section>

      <section className="admin-draft-form-section" aria-label="Advanced metadata">
        <Typography.Title level={5}>Advanced / metadata</Typography.Title>
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
              <Button
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
              />
            </div>
          ))}
        </Flex>
        <Button
          onClick={() => onCandidateChange(writeMetadataRows(candidate, [...metadataRows, { key: "", value: "" }]))}
          disabled={busy || metadataRows.some((row) => row.key === "")}
        >
          Add metadata
        </Button>
      </section>
    </Flex>
  );
}

function emptySkill(): SkillDraft {
  return {
    id: "",
    name: "",
    description: "",
    procedure: "",
    activationKeywords: "",
    requiredCapabilities: "",
    resourcePaths: ""
  };
}

function SkillCard({
  index,
  skill,
  disabled,
  onChange,
  onRemove
}: {
  index: number;
  skill: SkillDraft;
  disabled: boolean;
  onChange: (skill: SkillDraft) => void;
  onRemove: () => void;
}) {
  const number = index + 1;
  return (
    <div className="admin-skill-card" aria-label={`Skill ${number}`}>
      <Flex justify="space-between" align="center" gap={8} wrap="wrap">
        <Typography.Text strong>{skill.name.trim() || `Skill ${number}`}</Typography.Text>
        <Button
          danger
          type="text"
          icon={<DeleteOutlined />}
          aria-label={`Remove skill ${number}`}
          disabled={disabled}
          onClick={onRemove}
        />
      </Flex>
      <div className="admin-draft-field-grid">
        <TextField
          label={`Skill ${number} id`}
          hint="Lowercase id, such as refund.handle."
          value={skill.id}
          disabled={disabled}
          onChange={(id) => onChange({ ...skill, id })}
        />
        <TextField
          label={`Skill ${number} name`}
          value={skill.name}
          disabled={disabled}
          onChange={(name) => onChange({ ...skill, name })}
        />
      </div>
      <TextField
        label={`Skill ${number} description`}
        value={skill.description}
        disabled={disabled}
        onChange={(description) => onChange({ ...skill, description })}
      />
      <label className="admin-draft-field">
        <Typography.Text strong>{`Skill ${number} procedure`}</Typography.Text>
        <Input.TextArea
          aria-label={`Skill ${number} procedure`}
          rows={4}
          value={skill.procedure}
          disabled={disabled}
          className="admin-draft-instructions"
          onChange={(event) => onChange({ ...skill, procedure: event.target.value })}
        />
      </label>
      <TextField
        label={`Skill ${number} activation keywords`}
        hint="Comma-separated. Keywords select this skill; they do not grant authority."
        value={skill.activationKeywords}
        disabled={disabled}
        onChange={(activationKeywords) => onChange({ ...skill, activationKeywords })}
      />
      <TextField
        label={`Skill ${number} required capabilities`}
        hint="Comma-separated requirements, not grants. They do not add tools, credentials, or approval."
        value={skill.requiredCapabilities}
        disabled={disabled}
        onChange={(requiredCapabilities) => onChange({ ...skill, requiredCapabilities })}
      />
      <TextField
        label={`Skill ${number} resource paths`}
        hint="Comma-separated relative paths."
        value={skill.resourcePaths}
        disabled={disabled}
        onChange={(resourcePaths) => onChange({ ...skill, resourcePaths })}
      />
      {skill.id.trim().length === 0 ? (
        <Typography.Text type="danger">Skill id is required before publish.</Typography.Text>
      ) : null}
    </div>
  );
}

export type PublishedSkill = {
  id: string;
  name: string;
  requiredCapabilities: string[];
};

export function PublishedSkillList({ skills }: { skills?: PublishedSkill[] | null }) {
  if (!skills) {
    return null;
  }

  if (skills.length === 0) {
    return <Typography.Text type="secondary">No skills on this published version.</Typography.Text>;
  }

  return (
    <ul className="admin-published-skills" aria-label="Published skills">
      {skills.map((skill) => (
        <li key={skill.id}>
          <Typography.Text>
            {skill.name} ({skill.id})
          </Typography.Text>
          <Typography.Text type="secondary">
            {skill.requiredCapabilities.length === 0
              ? " No required capabilities. Requirements do not grant tools, credentials, or approval."
              : ` Requires ${skill.requiredCapabilities.join(", ")}. Requirements do not grant tools, credentials, or approval.`}
          </Typography.Text>
        </li>
      ))}
    </ul>
  );
}

function TextField({
  label,
  hint,
  value,
  disabled,
  onChange
}: {
  label: string;
  hint?: string;
  value: string;
  disabled: boolean;
  onChange: (value: string) => void;
}) {
  const hintId = useId();
  return (
    <label className="admin-draft-field">
      <Typography.Text strong>{label}</Typography.Text>
      <Input
        aria-label={label}
        aria-describedby={hint ? hintId : undefined}
        value={value}
        disabled={disabled}
        onChange={(event) => onChange(event.target.value)}
      />
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
  value,
  disabled,
  step,
  onChange
}: {
  label: string;
  hint?: string;
  value: number | null;
  disabled: boolean;
  step?: number;
  onChange: (value: number | null) => void;
}) {
  const hintId = useId();
  return (
    <label className="admin-draft-field">
      <Typography.Text strong>{label}</Typography.Text>
      <InputNumber
        aria-label={label}
        aria-describedby={hint ? hintId : undefined}
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
      <Typography.Text strong>{label}</Typography.Text>
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
  return (
    <Flex align="center" gap={12} className="admin-draft-switch-row">
      <Switch aria-label={label} checked={checked} disabled={disabled} onChange={onChange} />
      <Typography.Text strong>{label}</Typography.Text>
    </Flex>
  );
}
