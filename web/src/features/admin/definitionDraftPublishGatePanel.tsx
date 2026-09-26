import { Alert, Button, Descriptions, Flex, Input, List, Select, Spin, Tag, Typography } from "antd";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { AdminDefinitionDraft } from "../../services/adminApi";
import {
  getAdminDefinitionDraft,
  getAdminDefinitionDraftDiff,
  listAdminDefinitionEvaluationResults,
  listAdminDefinitionEvaluationScenarios,
  runAdminDefinitionEvaluationScenario,
  upsertAdminDefinitionEvaluationScenario,
  validateAdminDefinitionDraft,
  type AdminDefinitionDraftDiff,
  type AdminDefinitionDraftValidation,
  type AdminDefinitionEvaluationResult,
  type AdminDefinitionEvaluationScenario
} from "../../services/adminApi";
import { computePublishEligibility, type EvidenceLoadStatus } from "./definitionDraftPublishGate";

type Props = {
  activeDraft: AdminDefinitionDraft;
  dirty: boolean;
  busy: boolean;
  toolNames: string[];
  onDraftRevisionChange: (draft: AdminDefinitionDraft) => void;
  onError: (message: string | null) => void;
  onEligibilityChange: (eligible: boolean) => void;
};

export function DefinitionDraftPublishGatePanel({
  activeDraft,
  dirty,
  busy,
  toolNames,
  onDraftRevisionChange,
  onError,
  onEligibilityChange
}: Props) {
  const [loading, setLoading] = useState(false);
  const [validation, setValidation] = useState<AdminDefinitionDraftValidation | null>(null);
  const [diff, setDiff] = useState<AdminDefinitionDraftDiff | null>(null);
  const [scenarios, setScenarios] = useState<AdminDefinitionEvaluationScenario[]>([]);
  const [results, setResults] = useState<AdminDefinitionEvaluationResult[]>([]);
  const [evidenceLoadStatus, setEvidenceLoadStatus] = useState<EvidenceLoadStatus>("idle");
  const [scenarioId, setScenarioId] = useState("tool-offered");
  const [scenarioTitle, setScenarioTitle] = useState("Tool offered check");
  const [scenarioPrompt, setScenarioPrompt] = useState(
    "Run a Synthetic behavior check against this draft using the selected tool policy."
  );
  const [checkType, setCheckType] = useState<AdminDefinitionEvaluationScenario["checkType"]>("ToolOffered");
  const [toolName, setToolName] = useState("");
  const checkTypeOptions = useMemo(
    () =>
      [
        { value: "ToolOffered", label: "Tool offered" },
        { value: "ToolNotOffered", label: "Tool not offered" },
        { value: "ResourceBound", label: "Resource bound" },
        { value: "TriggerSchedulePermitted", label: "Trigger schedule permitted" },
        { value: "ExternalActionDenied", label: "External action denied" }
      ] as const,
    []
  );
  const toolFieldRequired = checkType !== "TriggerSchedulePermitted";
  const requestGenerationRef = useRef(0);

  const resetGateState = useCallback(() => {
    setValidation(null);
    setDiff(null);
    setScenarios([]);
    setResults([]);
    setEvidenceLoadStatus("idle");
    onEligibilityChange(false);
  }, [onEligibilityChange]);

  useEffect(() => {
    requestGenerationRef.current += 1;
    resetGateState();
  }, [activeDraft.draftId, resetGateState]);

  const reloadEvidence = useCallback(async () => {
    const draftId = activeDraft.draftId;
    const generation = ++requestGenerationRef.current;
    setLoading(true);
    setEvidenceLoadStatus("loading");
    onError(null);
    try {
      const [scenarioItems, resultItems] = await Promise.all([
        listAdminDefinitionEvaluationScenarios(draftId),
        listAdminDefinitionEvaluationResults(draftId)
      ]);
      if (generation !== requestGenerationRef.current) {
        return;
      }

      setScenarios(scenarioItems);
      setResults(resultItems);
      setEvidenceLoadStatus("ready");
    } catch (error) {
      if (generation !== requestGenerationRef.current) {
        return;
      }

      setScenarios([]);
      setResults([]);
      setEvidenceLoadStatus("failed");
      onError(error instanceof Error ? error.message : "Failed to load evaluation state.");
    } finally {
      if (generation === requestGenerationRef.current) {
        setLoading(false);
      }
    }
  }, [activeDraft.draftId, onError]);

  useEffect(() => {
    void reloadEvidence();
  }, [reloadEvidence, activeDraft.revision]);

  useEffect(() => {
    if (
      validation
      && (validation.draftId !== activeDraft.draftId || validation.draftRevision !== activeDraft.revision)
    ) {
      setValidation(null);
      setDiff(null);
    }
  }, [activeDraft.draftId, activeDraft.revision, validation]);

  useEffect(() => {
    if (
      diff
      && (diff.draftId !== activeDraft.draftId || diff.draftRevision !== activeDraft.revision)
    ) {
      setDiff(null);
    }
  }, [activeDraft.draftId, activeDraft.revision, diff]);

  const eligibility = useMemo(
    () =>
      computePublishEligibility({
        dirty,
        draftId: activeDraft.draftId,
        draftRevision: activeDraft.revision,
        evidenceLoadStatus,
        validation,
        diff,
        scenarios,
        results
      }),
    [
      dirty,
      activeDraft.draftId,
      activeDraft.revision,
      evidenceLoadStatus,
      validation,
      diff,
      scenarios,
      results
    ]
  );

  useEffect(() => {
    onEligibilityChange(eligibility.eligible);
  }, [eligibility.eligible, onEligibilityChange]);

  const runValidate = async () => {
    const draftId = activeDraft.draftId;
    const revision = activeDraft.revision;
    const generation = ++requestGenerationRef.current;
    setLoading(true);
    setValidation(null);
    setDiff(null);
    onError(null);
    try {
      const next = await validateAdminDefinitionDraft(draftId);
      if (generation !== requestGenerationRef.current) {
        return;
      }

      if (next.draftId !== draftId || next.draftRevision !== revision) {
        setValidation(null);
        onError("Validation response does not match the active draft.");
        return;
      }

      const nextDiff = await getAdminDefinitionDraftDiff(draftId);
      if (generation !== requestGenerationRef.current) {
        return;
      }

      if (nextDiff.draftId !== draftId || nextDiff.draftRevision !== revision) {
        setValidation(null);
        setDiff(null);
        onError("Diff response does not match the active draft revision.");
        return;
      }

      setValidation(next);
      setDiff(nextDiff);
      await reloadEvidence();
    } catch (error) {
      if (generation !== requestGenerationRef.current) {
        return;
      }

      setValidation(null);
      setDiff(null);
      onError(error instanceof Error ? error.message : "Validation failed.");
    } finally {
      if (generation === requestGenerationRef.current) {
        setLoading(false);
      }
    }
  };

  const saveScenario = async () => {
    if (!scenarioId.trim() || !scenarioPrompt.trim() || (toolFieldRequired && !toolName)) {
      onError("Scenario id, prompt, and required fields for the check type are required.");
      return;
    }
    setLoading(true);
    onError(null);
    try {
      await upsertAdminDefinitionEvaluationScenario(activeDraft.draftId, {
        expectedRevision: activeDraft.revision,
        scenarioId: scenarioId.trim(),
        title: scenarioTitle.trim() || scenarioId.trim(),
        prompt: scenarioPrompt.trim(),
        requirementLevel: "Required",
        checkType,
        toolName: toolFieldRequired ? toolName : null
      });
      onDraftRevisionChange(await getAdminDefinitionDraft(activeDraft.draftId));
      setValidation(null);
      setDiff(null);
      await reloadEvidence();
    } catch (error) {
      onError(error instanceof Error ? error.message : "Scenario save failed.");
    } finally {
      setLoading(false);
    }
  };

  const runScenario = async (id: string) => {
    setLoading(true);
    onError(null);
    try {
      await runAdminDefinitionEvaluationScenario(activeDraft.draftId, id);
      await reloadEvidence();
    } catch (error) {
      onError(error instanceof Error ? error.message : "Evaluation run failed.");
    } finally {
      setLoading(false);
    }
  };

  return (
    <Flex vertical gap={16} aria-label="Test validate and publish gate" className="admin-publish-gate">
      <div className="admin-draft-tab-intro">
        <Typography.Title level={5}>Test &amp; Publish</Typography.Title>
        <Typography.Paragraph type="secondary">
          Validate this exact revision, run every required Synthetic check, and review the safe diff before publishing.
        </Typography.Paragraph>
      </div>
      {loading ? <Spin size="small" /> : null}
      {!eligibility.eligible ? (
        <Alert
          type="warning"
          showIcon
          title="Publish blocked"
          description={
            <ul className="admin-publish-blockers">
              {eligibility.blockers.map((item) => (
                <li key={item}>{item}</li>
              ))}
            </ul>
          }
        />
      ) : (
        <Alert type="success" showIcon title="Draft is ready for final publish." />
      )}
      <Flex gap={8} wrap="wrap" className="admin-publish-actions">
        <Button type="primary" onClick={() => void runValidate()} disabled={busy || loading || dirty}>
          Run validation
        </Button>
        <Button onClick={() => void reloadEvidence()} disabled={busy || loading}>
          Refresh evaluations
        </Button>
      </Flex>
      {validation ? (
        <Descriptions bordered size="small" column={1} title="Validation snapshot" className="admin-validation-summary">
          <Descriptions.Item label="Draft">{validation.draftId}</Descriptions.Item>
          <Descriptions.Item label="Revision">{validation.draftRevision}</Descriptions.Item>
          <Descriptions.Item label="Fingerprint">{validation.configurationFingerprint}</Descriptions.Item>
          <Descriptions.Item label="Blocking findings">
            {validation.hasBlockingFindings ? "Yes" : "No"}
          </Descriptions.Item>
        </Descriptions>
      ) : null}
      {validation && validation.findings.length > 0 ? (
        <List
          size="small"
          bordered
          header="Findings"
          dataSource={validation.findings}
          renderItem={(item) => (
            <List.Item>
              <Typography.Text>
                [{item.severity}] {item.field} · {item.code}: {item.message}
              </Typography.Text>
            </List.Item>
          )}
        />
      ) : null}
      <section className="admin-draft-form-section" aria-label="Required evaluation scenario">
        <Typography.Title level={5}>Required evaluation scenario</Typography.Title>
        <Typography.Paragraph type="secondary">
          Save a deterministic check for the behavior this definition must satisfy before publication.
        </Typography.Paragraph>
        <div className="admin-eval-form-grid">
        <label className="admin-draft-field">
          <Typography.Text strong>Scenario ID</Typography.Text>
          <Input
            aria-label="Evaluation scenario id"
            value={scenarioId}
            onChange={(event) => setScenarioId(event.target.value)}
            disabled={busy || loading}
          />
        </label>
        <label className="admin-draft-field">
          <Typography.Text strong>Title</Typography.Text>
          <Input
            aria-label="Evaluation scenario title"
            value={scenarioTitle}
            onChange={(event) => setScenarioTitle(event.target.value)}
            disabled={busy || loading}
          />
        </label>
        <label className="admin-draft-field">
          <Typography.Text strong>Check type</Typography.Text>
          <Select
            aria-label="Evaluation check type"
            value={checkType}
            onChange={setCheckType}
            options={checkTypeOptions.map((option) => ({ value: option.value, label: option.label }))}
            disabled={busy || loading}
          />
        </label>
        <label className="admin-draft-field admin-eval-prompt">
          <Typography.Text strong>Prompt</Typography.Text>
          <Input
            aria-label="Evaluation scenario prompt"
            value={scenarioPrompt}
            onChange={(event) => setScenarioPrompt(event.target.value)}
            disabled={busy || loading}
          />
        </label>
        {toolFieldRequired ? (
          <label className="admin-draft-field">
            <Typography.Text strong>{checkType === "ResourceBound" ? "Resource path" : "Tool"}</Typography.Text>
            {checkType === "ResourceBound" ? (
              <Input
                aria-label="Evaluation resource logical path"
                value={toolName}
                onChange={(event) => setToolName(event.target.value)}
                disabled={busy || loading}
                placeholder="brief.md"
              />
            ) : (
              <Select
                aria-label="Evaluation tool name"
                showSearch
                optionFilterProp="label"
                value={toolName || undefined}
                onChange={setToolName}
                options={toolNames.map((name) => ({ value: name, label: name }))}
                disabled={busy || loading}
                placeholder="Select tool"
              />
            )}
          </label>
        ) : null}
        </div>
        <Button onClick={() => void saveScenario()} disabled={busy || loading || dirty}>
          Save required scenario
        </Button>
      </section>
      <section aria-label="Saved evaluation scenarios">
        <Flex align="baseline" justify="space-between" gap={12} className="admin-draft-section-heading">
          <Typography.Title level={5}>Saved scenarios</Typography.Title>
          <Typography.Text type="secondary">{scenarios.length}</Typography.Text>
        </Flex>
        {scenarios.length > 0 ? (
          <List
            size="small"
            bordered
            className="admin-evaluation-list"
            dataSource={scenarios}
            renderItem={(item) => {
              const latest = results
                .filter((result) => result.scenarioId === item.scenarioId)
                .sort((a, b) => b.recordedAt.localeCompare(a.recordedAt))[0];
              return (
                <List.Item
                  actions={[
                    <Button key="run" size="small" onClick={() => void runScenario(item.scenarioId)} disabled={busy || loading}>
                      Run Synthetic
                    </Button>
                  ]}
                >
                  <Flex vertical gap={8}>
                    <Flex gap={8} wrap="wrap" align="center">
                      <Typography.Text strong>{item.title}</Typography.Text>
                      <Tag>{item.requirementLevel}</Tag>
                      <Tag>{item.checkType}</Tag>
                    </Flex>
                    <Typography.Text type="secondary">{item.prompt}</Typography.Text>
                    {latest ? (
                      <Tag color={latest.passed ? "success" : "error"}>
                        {latest.passed ? "Passed" : "Failed"} at revision {latest.draftRevision}
                      </Tag>
                    ) : (
                      <Typography.Text type="secondary">Not run yet</Typography.Text>
                    )}
                  </Flex>
                </List.Item>
              );
            }}
          />
        ) : (
          <Typography.Text type="secondary">No evaluation scenarios yet.</Typography.Text>
        )}
      </section>
      {diff ? (
        <List
          size="small"
          bordered
          header={`Diff vs ${diff.baselineKind}${diff.baselineVersion != null ? ` v${diff.baselineVersion}` : ""}`}
          dataSource={diff.sections}
          renderItem={(item) => (
            <List.Item>
              <Flex vertical gap={4}>
                <Typography.Text strong>{item.label} ({item.changeKind})</Typography.Text>
                {item.beforeSummary ? (
                  <Typography.Text type="secondary">Before: {item.beforeSummary}</Typography.Text>
                ) : null}
                {item.afterSummary ? (
                  <Typography.Text>After: {item.afterSummary}</Typography.Text>
                ) : null}
              </Flex>
            </List.Item>
          )}
        />
      ) : null}
    </Flex>
  );
}
