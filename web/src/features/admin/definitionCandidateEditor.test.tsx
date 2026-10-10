import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { useState } from "react";
import { applyCandidateJson, candidateForPersistence, candidateToJson, type DefinitionCandidate } from "./definitionCandidate";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { AdminApp } from "./AdminApp";
import { DefinitionCandidateEditor } from "./definitionCandidateEditor";
import { DefinitionSkillsSection } from "./DefinitionSkillsSection";

vi.mock("../../services/adminApi", () => ({
  getExecutionBudgetLimits: vi.fn().mockResolvedValue({ maxSteps: 144, durationSeconds: 900, perToolSeconds: 30 }),
  listAdminDefinitions: vi.fn(),
  listAdminInstances: vi.fn(),
  getAdminEffectiveConfig: vi.fn(),
  listAdminDefinitionDrafts: vi.fn(),
  listAdminDefinitionPublications: vi.fn(),
  getAdminDefinitionDraft: vi.fn(),
  deleteAdminDefinitionDraft: vi.fn(),
  updateAdminDefinitionDraft: vi.fn(),
  publishAdminDefinitionDraft: vi.fn(),
  createNewAdminDefinitionDraft: vi.fn(),
  createAdminAgentInstance: vi.fn(),
  forkAdminDefinitionDraft: vi.fn(),
  listAdminDraftResources: vi.fn(),
  listAdminPublicationResources: vi.fn(),
  getAdminToolRegistry: vi.fn().mockResolvedValue({ toolNames: ["workspace.read"], maxToolAllowlistEntries: 32 }),
  uploadAdminDraftResourceContent: vi.fn(),
  upsertAdminDraftResource: vi.fn(),
  bindAdminDraftResources: vi.fn(),
  removeAdminDraftResource: vi.fn(),
  updateAdminAgentInstancePersona: vi.fn(),
  updateAdminAgentInstanceLifecycle: vi.fn(),
  updateAdminAgentInstanceActiveVersion: vi.fn(),
  deprecateAdminDefinitionPublication: vi.fn(),
  validateAdminDefinitionDraft: vi.fn(),
  getAdminDefinitionDraftDiff: vi.fn(),
  listAdminDefinitionEvaluationScenarios: vi.fn(),
  listAdminDefinitionEvaluationResults: vi.fn(),
  listAdminAuthoringOptions: vi.fn()
}));

import {
  getAdminDefinitionDraft,
  listAdminDefinitionDrafts,
  listAdminDefinitionPublications,
  listAdminDefinitions,
  listAdminDraftResources,
  listAdminInstances,
  listAdminAuthoringOptions,
  getAdminToolRegistry,
  publishAdminDefinitionDraft,
  updateAdminDefinitionDraft
} from "../../services/adminApi";

const authoringOptions = {
  languageModelAliases: ["backup-llm", "primary-llm"],
  speechRecognizerAliases: ["primary-stt"],
  speechSynthesizerAliases: ["primary-tts"],
  defaultLanguageModelAlias: "primary-llm",
  defaultSpeechRecognizerAlias: "primary-stt",
  defaultSpeechSynthesizerAlias: "primary-tts",
  defaultModelKey: "scripted-alpha",
  models: [
    {
      key: "scripted-alpha",
      displayName: "Scripted Alpha",
      supportedReasoningEfforts: ["low", "medium", "high"],
      defaultReasoningEffort: "medium"
    },
    {
      key: "scripted-beta",
      displayName: "Scripted Beta",
      supportedReasoningEfforts: [],
      defaultReasoningEffort: null
    }
  ],
  interruptionClassifiers: ["heuristic"]
};

const draftId = "019944af-00d1-7000-8000-0000000000c1";

const storedCandidate = {
  schemaVersion: 1,
  definitionId: "examiner",
  identity: { name: "Alex", role: "Examiner", description: "Practice speaking.", tone: "Calm" },
  goals: ["Assess the speaker"],
  systemInstructions: "Stored body",
  behaviorPolicy: {
    interruptionStyle: "acknowledgeThenContinue",
    acknowledgeInterruption: false,
    avoidUnsupportedClaims: false
  },
  conversationPolicy: {
    responseLength: "concise",
    askOneQuestionAtATime: false,
    language: "auto",
    maxOutputTokens: 256
  },
  initiativePolicy: {
    enabled: false,
    silenceThresholdMs: 3000,
    cooldownMs: 15000,
    maxPerSilencePeriod: 1,
    triggers: ["longSilence"],
    maxConsecutiveProactiveTurns: 1,
    maxSilentEvaluations: 8,
    maxInactivityMs: 900000
  },
  modelDefaults: { catalogKey: "scripted-alpha", reasoningEffort: "low" },
  providerPreferences: {
    languageModel: "primary-llm",
    speechRecognizer: null,
    speechSynthesizer: null,
    interruptionClassifier: "heuristic"
  },
  voice: { enabled: false, voiceId: "verse", speakingRate: 1 },
  memoryPolicy: {
    sessionMemory: false,
    identityUserPromotion: false,
    identityUserRetrieval: false,
    userPromotion: false,
    userRetrieval: false
  },
  triggerPolicy: {
    enabled: false,
    allowUserScheduling: false,
    allowOneShot: false,
    allowDaily: false,
    allowWeekly: false,
    allowIndefiniteRecurrence: false,
    maxActiveRegistrations: 4,
    oneShotHorizonDays: 30,
    minRecurrenceDays: 1,
    allowedSourceKinds: ["schedule"],
    allowFixedInterval: false,
    minFixedIntervalSeconds: 60
  },
  metadata: { owner: "kept" },
  environment: {
    harness: ["examiner-turn-taking"],
    toolAllowlist: [],
    knowledgeSources: [],
    workspace: { templateId: "" },
    attachments: { allowUnreadUnsupportedTypes: false }
  },
  untouchedMarker: { nested: "preserve-me" }
};

function mockDraft(candidate: Record<string, unknown> = storedCandidate) {
  vi.mocked(listAdminDefinitions).mockResolvedValue([
    {
      definitionId: "examiner",
      version: 1,
      source: "builtIn",
      status: "published",
      displayName: "Examiner"
    }
  ]);
  vi.mocked(listAdminInstances).mockResolvedValue([]);
  vi.mocked(listAdminDefinitionDrafts).mockResolvedValue([
    {
      draftId,
      definitionId: "examiner",
      revision: 2,
      sourceKind: "ForkBuiltIn",
      sourceVersion: 1,
      updatedAt: "2026-01-01T00:00:00Z"
    }
  ]);
  vi.mocked(listAdminDefinitionPublications).mockResolvedValue([]);
  vi.mocked(listAdminDraftResources).mockResolvedValue([]);
  vi.mocked(getAdminToolRegistry).mockResolvedValue({ toolNames: ["workspace.read", "knowledge.retrieve"], maxToolAllowlistEntries: 32 });
  vi.mocked(listAdminAuthoringOptions).mockResolvedValue(authoringOptions);
  vi.mocked(getAdminDefinitionDraft).mockResolvedValue({
    draftId,
    definitionId: "examiner",
    revision: 2,
    sourceKind: "ForkBuiltIn",
    sourceVersion: 1,
    createdAt: "2026-01-01T00:00:00Z",
    updatedAt: "2026-01-01T00:00:00Z",
    candidate
  });
  vi.mocked(updateAdminDefinitionDraft).mockResolvedValue({
    draftId,
    definitionId: "examiner",
    revision: 3,
    sourceKind: "ForkBuiltIn",
    sourceVersion: 1,
    createdAt: "2026-01-01T00:00:00Z",
    updatedAt: "2026-01-02T00:00:00Z",
    candidate
  });
  vi.mocked(publishAdminDefinitionDraft).mockResolvedValue({
    definitionId: "examiner",
    version: 2,
    status: "published",
    metadataRevision: 1,
    publishedAt: "2026-01-02T00:00:00Z"
  });
}

async function openDraft() {
  await act(async () => {
    render(<AdminApp route={{ area: "admin", view: "definition", definitionId: "examiner" }} />);
  });
  fireEvent.click(await screen.findByRole("tab", { name: "Drafts" }));
  await waitFor(() => {
    expect(screen.getByRole("button", { name: /Draft rev 2/ })).toBeInTheDocument();
  });
  await act(async () => { fireEvent.click(screen.getByRole("button", { name: /Draft rev 2/ })); });
  openSettings();
  await waitFor(() => {
    expect(screen.getByLabelText("System instructions")).toBeInTheDocument();
    expect(screen.getByTitle("Scripted Alpha")).toBeInTheDocument();
  });
}

// Exercise the controlled form without loading inventory, version tables, resources,
// and publication gates. The AdminApp tests below retain save/revision and tab wiring.
async function renderCandidate(candidate: DefinitionCandidate = storedCandidate) {
  vi.mocked(listAdminAuthoringOptions).mockResolvedValue(authoringOptions);
  const changed = vi.fn<(candidate: DefinitionCandidate) => void>();
  function ControlledEditor() {
    const [value, setValue] = useState(candidate);
    const [view, setView] = useState<"form" | "json">("form");
    return <DefinitionCandidateEditor
      candidate={value} view={view} jsonText={candidateToJson(value)} busy={false}
      onCandidateChange={next => { changed(next); setValue(next); }}
      onJsonTextChange={text => {
        const parsed = applyCandidateJson(text);
        if (parsed.ok) { changed(parsed.candidate); setValue(parsed.candidate); }
      }}
      onViewChange={setView}
    />;
  }
  await act(async () => { render(<ControlledEditor />); });
  openSettings();
  expect(await screen.findByTitle("Scripted Alpha")).toBeInTheDocument();
  return changed;
}

function openSettings(...sections: string[]) {
  fireEvent.click(screen.getByRole("tab", { name: "Settings" }));
  const names = new Set(sections.length ? sections : ["Operating instructions", "Model defaults"]);
  const headers = screen.getAllByRole("button").filter(header => names.has(header.textContent?.trim() ?? "") && header.getAttribute("aria-expanded") === "false");
  for (const header of headers) fireEvent.click(header);
}

async function chooseOption(label: string, optionName: string) {
  fireEvent.mouseDown(screen.getByLabelText(label));
  const option = await screen.findByTitle(optionName);
  await act(async () => { fireEvent.click(option); });
}

function setSpin(label: string, value: string) {
  fireEvent.change(screen.getByLabelText(label), { target: { value } });
}

function setText(label: string, value: string) {
  fireEvent.change(screen.getByLabelText(label), { target: { value } });
}

describe("definition candidate editor", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it.each(["coreEvent", "applicationEvent"])("preserves restricted %s authority through unrelated editing and JSON round trips", async kind => {
    const candidate = structuredClone(storedCandidate);
    candidate.triggerPolicy.allowedSourceKinds = [kind];
    const changed = await renderCandidate(candidate);
    expect(screen.getByRole("checkbox", { name: "Events" })).toHaveAttribute("aria-checked", "mixed");
    setText("Definition name", "Edited name");
    fireEvent.click(screen.getByText("Advanced JSON", { selector: ".ant-segmented-item-label" }));
    const json = screen.getByRole("textbox", { name: "Advanced JSON" });
    const value = JSON.parse((json as HTMLTextAreaElement).value);
    expect(value.triggerPolicy.allowedSourceKinds).toEqual([kind]);
    value.triggerPolicy.allowedSourceKinds = ["schedule", kind];
    fireEvent.change(json, { target: { value: JSON.stringify(value) } });
    fireEvent.click(screen.getByText("Form", { selector: ".ant-segmented-item-label" }));
    expect(screen.getByRole("checkbox", { name: "Schedule" })).toBeChecked();
    expect(screen.getByRole("checkbox", { name: "Events" })).toHaveAttribute("aria-checked", "mixed");
    expect(changed.mock.lastCall?.[0]).toMatchObject({ triggerPolicy: { allowedSourceKinds: ["schedule", kind] } });
  });

  it("blocks fractional recurrence with an inline error and saves after correction", async () => {
    mockDraft();
    await openDraft();
    // These controls stay mounted across draft tabs. Reuse their accessible
    // handles instead of rescanning the entire AntD form after every change.
    const save = screen.getByRole("button", { name: "Save draft" });
    const capabilitiesTab = screen.getByRole("tab", { name: "Capabilities" });
    const definitionTab = screen.getByRole("tab", { name: "Identity & version" });
    openSettings("Trigger restrictions");
    setSpin("Minimum recurrence days", "0.5");
    expect(screen.getByLabelText("Minimum recurrence days")).toHaveAttribute("aria-invalid", "true");
    expect(screen.getByLabelText("Minimum recurrence days")).toHaveAccessibleDescription(
      "Whole days. 1 to 365. Minimum recurrence days must be a whole number from 1 to 365.");
    expect(save).toBeDisabled();
    fireEvent.click(capabilitiesTab);
    expect(save).toBeInTheDocument();
    expect(save).toBeDisabled();
    expect(updateAdminDefinitionDraft).not.toHaveBeenCalled();
    fireEvent.click(definitionTab);
    setSpin("Minimum recurrence days", "2");
    expect(screen.getByLabelText("Minimum recurrence days")).not.toHaveAttribute("aria-invalid", "true");
    expect(save).toBeEnabled();
    fireEvent.click(save);
    await waitFor(() => expect(updateAdminDefinitionDraft).toHaveBeenCalledWith(
      draftId, 2, expect.objectContaining({ triggerPolicy: expect.objectContaining({ minRecurrenceDays: 2 }) })));
  });

  it("validates and adds a Skill while preserving inherited content", async () => {
    const initial = {
      ...storedCandidate,
      skills: [
        {
          id: "order.lookup",
          name: "Order lookup",
          description: "Look up an order",
          procedure: "ORDER_PROCEDURE",
          projection: "OnDemand", defaultEnabled: true,
          requiredCapabilities: ["web.search"],
          resourcePaths: []
        }
      ]
    };
    const changed = vi.fn<(candidate: DefinitionCandidate) => void>();
    function ControlledSkills() {
      const [value, setValue] = useState<DefinitionCandidate>(initial);
      return <DefinitionSkillsSection candidate={value} busy={false} readOnly={false}
        onChange={next => { changed(next); setValue(next); }} />;
    }
    await act(async () => { render(<ControlledSkills />); });

    expect(screen.getByText(/Required capabilities are requirements, not grants/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Details" }));
    expect(await screen.findByText("ORDER_PROCEDURE")).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "Close inspection" }));
    fireEvent.click(screen.getByRole("button", { name: "Add skill" }));
    fireEvent.click(screen.getByRole("button", { name: "Save Skill" }));
    expect(await screen.findByText("Please enter Skill name", {}, { timeout: 5000 })).toBeVisible();
    setText("Skill ID", "refund.handle");
    setText("Skill name", "Refund");
    setText("Procedure", "REFUND_PROCEDURE");
    setText("Description", "refund, return");
    setText("Required capabilities", "workspace.read, chat.respond");
    setText("Resource paths", "notes/refund.md");
    fireEvent.click(screen.getByRole("button", { name: "Save Skill" }));
    await waitFor(() => expect(screen.getByText("Refund")).toBeInTheDocument());
    const saved = candidateForPersistence(changed.mock.lastCall![0]) as {
      skills: Array<{ id: string; procedure: string; requiredCapabilities: string[]; resourcePaths: string[] }>;
    };
    expect(saved.skills[0]).toEqual(initial.skills[0]);
    expect(saved.skills[1]).toMatchObject({ id: "refund.handle", procedure: "REFUND_PROCEDURE",
      requiredCapabilities: ["workspace.read", "chat.respond"], resourcePaths: ["notes/refund.md"] });
  });

  it("round-trips Skills through form and JSON without changing inherited content", async () => {
    const inherited = { id: "order.lookup", name: "Order lookup", description: "Look up an order",
      procedure: "ORDER_PROCEDURE", projection: "OnDemand", defaultEnabled: true,
      requiredCapabilities: ["web.search"], resourcePaths: [] };
    const refund = { id: "refund.handle", name: "Refund", description: "refund, return",
      procedure: "REFUND_PROCEDURE", projection: "OnDemand", defaultEnabled: true,
      requiredCapabilities: ["workspace.read", "chat.respond"], resourcePaths: ["notes/refund.md"] };
    const changed = await renderCandidate({ ...storedCandidate, skills: [inherited, refund] });
    fireEvent.click(screen.getByRole("radio", { name: "Advanced JSON" }));
    const json = screen.getByRole("textbox", { name: "Advanced JSON" }) as HTMLTextAreaElement;
    expect(json.value).toContain("REFUND_PROCEDURE");
    expect(json.value).toContain("ORDER_PROCEDURE");
    fireEvent.change(json, { target: { value: json.value.replace("REFUND_PROCEDURE", "REFUND_PROCEDURE_EDITED") } });
    fireEvent.click(screen.getByRole("radio", { name: "Form" }));
    openSettings();

    const saved = candidateForPersistence(changed.mock.lastCall![0]) as { skills: unknown[] };
    expect(saved.skills).toEqual([inherited, { ...refund, procedure: "REFUND_PROCEDURE_EDITED" }]);
  });

  it("orders descending model efforts without rewriting the selected candidate", async () => {
    const descending = { ...authoringOptions, models: authoringOptions.models.map(model => ({ ...model,
      supportedReasoningEfforts: [...model.supportedReasoningEfforts].reverse() })) };
    vi.mocked(listAdminAuthoringOptions).mockResolvedValue(descending);
    const changed = vi.fn();
    render(<DefinitionCandidateEditor candidate={storedCandidate} view="form" jsonText={candidateToJson(storedCandidate)}
      busy={false} onCandidateChange={changed} onJsonTextChange={vi.fn()} onViewChange={vi.fn()} />);
    openSettings();
    await screen.findByTitle("Scripted Alpha");
    fireEvent.mouseDown(screen.getByLabelText("Reasoning"));
    await screen.findByRole("listbox");
    expect([...document.querySelectorAll(".ant-select-dropdown:not(.ant-select-dropdown-hidden) .ant-select-item-option-content")]
      .map(option => option.textContent)).toEqual(["Low", "Medium", "High"]);
    expect(changed).not.toHaveBeenCalled();
    fireEvent.click(screen.getByTitle("High"));
    expect(changed.mock.lastCall![0].modelDefaults.reasoningEffort).toBe("high");
  });

  it("edits every supported candidate area through the form and saves that candidate", async () => {
    mockDraft({ ...storedCandidate, skills: [{
      id: "refund.handle", name: "Refund", description: "Handle a refund",
      procedure: "REFUND_PROCEDURE", projection: "OnDemand", defaultEnabled: true,
      requiredCapabilities: ["workspace.read", "chat.respond"], resourcePaths: ["notes/refund.md"]
    }] });
    await openDraft();

    openSettings("Conversation", "Behavior", "Initiative", "Voice", "Memory policy", "Trigger restrictions", "Provider preferences", "Advanced / metadata");
    expect(screen.getByLabelText("Definition ID")).toHaveTextContent("examiner");
    expect(screen.queryByRole("textbox", { name: "Definition ID" })).not.toBeInTheDocument();
    expect(screen.getByLabelText("Silence threshold")).toHaveAccessibleDescription(
      "Milliseconds. 1,000 to 120,000."
    );
    expect(screen.getByLabelText("Cooldown")).toHaveAccessibleDescription(
      "Milliseconds. 5,000 to 600,000."
    );
    expect(screen.getByLabelText("Max inactivity")).toHaveAccessibleDescription(
      "Milliseconds. Empty uses 900,000."
    );
    expect(screen.getByLabelText("Silence threshold")).toHaveValue("3000");
    expect(screen.getByLabelText("Conversation language")).toHaveAccessibleDescription(
      "auto, or a BCP 47 tag such as en."
    );
    expect(screen.getByTitle("Scripted Alpha")).toBeInTheDocument();
    expect(screen.getByText(/Speech aliases are required when voice is on/)).toBeInTheDocument();

    fireEvent.click(screen.getByRole("tab", { name: "Profile" }));
    setText("Definition name", "Guide");
    setText("Definition role", "Coach");
    setText("Definition description", "Helps the operator.");
    setText("Definition tone", "Direct");
    setText("Goal 1", "Coach the operator");
    fireEvent.click(screen.getByRole("button", { name: "Add goal" }));
    setText("Goal 2", "Keep answers short");
    openSettings();
    setText("System instructions", "Edited instructions");
    fireEvent.click(screen.getByRole("tab", { name: "Skills & resources" }));
    fireEvent.click(screen.getByRole("button", { name: "Edit" }));
    setText("Procedure", "REFUND_PROCEDURE_EDITED");
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Save Skill" })); });
    fireEvent.click(screen.getByRole("tab", { name: "Identity & version" }));
    await chooseOption("Interruption style", "Answer the new turn");
    fireEvent.click(screen.getByLabelText("Acknowledge interruption"));
    fireEvent.click(screen.getByLabelText("Avoid unsupported claims"));
    await chooseOption("Response length", "Balanced");
    setText("Conversation language", "en");
    setSpin("Max output tokens", "512");
    fireEvent.click(screen.getByLabelText("Ask one question at a time"));
    fireEvent.click(screen.getByLabelText("Initiative enabled"));
    setSpin("Silence threshold", "4000");
    setSpin("Cooldown", "20000");
    setSpin("Max prompts per silence", "2");
    setSpin("Max consecutive proactive turns", "3");
    setSpin("Max silent evaluations", "9");
    setSpin("Max inactivity", "120000");
    await chooseOption("Initiative triggers", "Environment update");
    await chooseOption("Reasoning", "Medium");
    await chooseOption("Model", "Scripted Beta");
    await chooseOption("Language-model provider", "backup-llm");
    openSettings("Voice", "Provider preferences");
    fireEvent.click(screen.getByLabelText("Voice enabled"));
    setText("Voice id", "alloy");
    setSpin("Speaking rate", "1.2");
    fireEvent.click(screen.getByLabelText("Session memory"));
    fireEvent.click(screen.getByLabelText("Identity user promotion"));
    fireEvent.click(screen.getByLabelText("Identity user retrieval"));
    fireEvent.click(screen.getByLabelText("User promotion"));
    fireEvent.click(screen.getByLabelText("User retrieval"));
    fireEvent.click(screen.getByLabelText("Automation enabled"));
    fireEvent.click(screen.getByLabelText("Allow user scheduling"));
    fireEvent.click(screen.getByLabelText("Allow one-shot"));
    fireEvent.click(screen.getByLabelText("Allow daily"));
    fireEvent.click(screen.getByLabelText("Allow weekly"));
    fireEvent.click(screen.getByLabelText("Allow indefinite recurrence"));
    fireEvent.click(screen.getByLabelText("Allow fixed interval"));
    setSpin("Max active registrations", "8");
    setSpin("One-shot horizon days", "14");
    setSpin("Minimum recurrence days", "2");
    setSpin("Minimum fixed interval seconds", "120");
    fireEvent.click(screen.getByRole("checkbox", { name: "Webhook Events" }));
    setText("Metadata key 1", "team");
    setText("Metadata value 1", "platform");

    await act(async () => { fireEvent.click(screen.getByRole("tab", { name: "Capabilities" })); });
    const harness = screen.getByLabelText("Harness labels");
    fireEvent.mouseDown(harness);
    fireEvent.change(harness, { target: { value: "lab-harness" } });
    fireEvent.keyDown(harness, { key: "Enter", code: "Enter", keyCode: 13 });
    await chooseOption("Authorized capabilities", "workspace.read");
    fireEvent.click(screen.getByRole("button", { name: "Add knowledge source" }));
    setText("Knowledge identity 1", "refund-policy");
    setText("Knowledge title 1", "Refund Policy");
    setText("Knowledge citation 1", "refund-policy@v3");
    setText("Workspace template id", "examiner-default");
    fireEvent.click(screen.getByLabelText("Allow unread unsupported attachment types"));

    await act(async () => { fireEvent.click(screen.getByRole("tab", { name: "Identity & version" })); });
    fireEvent.click(screen.getByRole("radio", { name: "Advanced JSON" }));
    const json = screen.getByRole("textbox", { name: "Advanced JSON" }) as HTMLTextAreaElement;
    const parsed = JSON.parse(json.value) as {
      identity: { name: string; role: string; description: string; tone: string };
      goals: string[];
      systemInstructions: string;
      behaviorPolicy: { interruptionStyle: string; acknowledgeInterruption: boolean; avoidUnsupportedClaims: boolean };
      conversationPolicy: {
        responseLength: string;
        askOneQuestionAtATime: boolean;
        language: string;
        maxOutputTokens: number;
      };
      initiativePolicy: {
        enabled: boolean;
        silenceThresholdMs: number;
        cooldownMs: number;
        maxPerSilencePeriod: number;
        triggers: string[];
        maxConsecutiveProactiveTurns: number;
        maxSilentEvaluations: number;
        maxInactivityMs: number;
      };
      modelDefaults: { catalogKey: string; reasoningEffort: string };
      providerPreferences: {
        languageModel: string;
        speechRecognizer: string;
        speechSynthesizer: string;
        interruptionClassifier: string;
      };
      voice: { enabled: boolean; voiceId: string; speakingRate: number };
      memoryPolicy: Record<string, boolean>;
      triggerPolicy: Record<string, unknown>;
      metadata: { team: string };
      environment: {
        harness: string[];
        toolAllowlist?: string[];
        capabilities: { mode: string; resolvedCapabilities: string[] };
        knowledgeSources: Array<{ identity: string; title: string; citation: string }>;
        workspace: { templateId: string };
        attachments: { allowUnreadUnsupportedTypes: boolean };
      };
      untouchedMarker: { nested: string };
      definitionId: string;
    };

    expect(parsed.definitionId).toBe("examiner");
    expect(parsed.identity).toEqual({
      name: "Guide",
      role: "Coach",
      description: "Helps the operator.",
      tone: "Direct"
    });
    expect(parsed.goals).toEqual(["Coach the operator", "Keep answers short"]);
    expect(parsed.systemInstructions).toBe("Edited instructions");
    expect(parsed.behaviorPolicy).toMatchObject({
      interruptionStyle: "answerNewTurn",
      acknowledgeInterruption: true,
      avoidUnsupportedClaims: true
    });
    expect(parsed.conversationPolicy).toMatchObject({
      responseLength: "balanced",
      askOneQuestionAtATime: true,
      language: "en",
      maxOutputTokens: 512
    });
    expect(parsed.initiativePolicy).toMatchObject({
      enabled: true,
      silenceThresholdMs: 4000,
      cooldownMs: 20000,
      maxPerSilencePeriod: 2,
      maxConsecutiveProactiveTurns: 3,
      maxSilentEvaluations: 9,
      maxInactivityMs: 120000
    });
    expect(parsed.initiativePolicy.triggers).toEqual(expect.arrayContaining(["longSilence", "environmentUpdate"]));
    expect(parsed.modelDefaults).toEqual({ catalogKey: "scripted-beta", reasoningEffort: null });
    expect(parsed.providerPreferences).toEqual({
      languageModel: "backup-llm",
      speechRecognizer: "primary-stt",
      speechSynthesizer: "primary-tts",
      interruptionClassifier: "heuristic"
    });
    expect(parsed.voice).toMatchObject({ enabled: true, voiceId: "alloy", speakingRate: 1.2 });
    expect(parsed.memoryPolicy).toMatchObject({
      sessionMemory: true,
      identityUserPromotion: true,
      identityUserRetrieval: true,
      userPromotion: true,
      userRetrieval: true
    });
    expect(parsed.triggerPolicy).toMatchObject({
      enabled: true,
      allowUserScheduling: true,
      allowOneShot: true,
      allowDaily: true,
      allowWeekly: true,
      allowIndefiniteRecurrence: true,
      allowFixedInterval: true,
      maxActiveRegistrations: 8,
      oneShotHorizonDays: 14,
      minRecurrenceDays: 2,
      minFixedIntervalSeconds: 120
    });
    expect(parsed.triggerPolicy.allowedSourceKinds).toEqual(
      expect.arrayContaining(["schedule", "applicationEvent"])
    );
    expect(parsed.metadata).toEqual({ team: "platform" });
    expect(parsed.environment.harness).toEqual(expect.arrayContaining(["examiner-turn-taking", "lab-harness"]));
    expect(parsed.environment.toolAllowlist).toBeUndefined();
    expect(parsed.environment.capabilities).toEqual({ mode: "Selected", resolvedCapabilities: ["workspace.read"] });
    expect(parsed.environment.knowledgeSources).toEqual([
      { identity: "refund-policy", title: "Refund Policy", citation: "refund-policy@v3" }
    ]);
    expect(parsed.environment.workspace.templateId).toBe("examiner-default");
    expect(parsed.environment.attachments.allowUnreadUnsupportedTypes).toBe(true);
    expect(parsed.untouchedMarker).toEqual({ nested: "preserve-me" });

    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Save draft" })); });
    await waitFor(() => {
      expect(updateAdminDefinitionDraft).toHaveBeenCalledWith(draftId, 2, expect.any(Object));
    });
    expect(vi.mocked(updateAdminDefinitionDraft).mock.calls.at(-1)?.[2]).toEqual({
      ...parsed,
      skills: [{ id: "refund.handle", name: "Refund", description: "Handle a refund",
        procedure: "REFUND_PROCEDURE_EDITED", projection: "OnDemand", defaultEnabled: true,
        requiredCapabilities: ["workspace.read", "chat.respond"], resourcePaths: ["notes/refund.md"] }]
    });
    expect(publishAdminDefinitionDraft).not.toHaveBeenCalled();
    // This integration walks every field and the revision-protected save.
    // Leaf round-trips above avoid repeatedly loading the Admin shell.
  }, 180_000);

  it("emits a voice-enabled candidate with default speech aliases", async () => {
    const changed = await renderCandidate();

    openSettings("Voice", "Provider preferences");
    fireEvent.click(screen.getByLabelText("Voice enabled"));
    expect(screen.getByTitle("primary-stt")).toBeInTheDocument();
    expect(screen.getByTitle("primary-tts")).toBeInTheDocument();

    const saved = changed.mock.lastCall?.[0] as {
      voice?: { enabled?: boolean };
      providerPreferences?: { speechRecognizer?: string; speechSynthesizer?: string };
    };
    expect(saved.voice?.enabled).toBe(true);
    expect(saved.providerPreferences).toMatchObject({
      speechRecognizer: "primary-stt",
      speechSynthesizer: "primary-tts"
    });
  });

  it("clears speech aliases when voice is turned off", async () => {
    mockDraft({
      ...storedCandidate,
      voice: { enabled: true, voiceId: "verse", speakingRate: 1 },
      providerPreferences: {
        languageModel: "primary-llm",
        speechRecognizer: "primary-stt",
        speechSynthesizer: "primary-tts",
        interruptionClassifier: "heuristic"
      }
    });

    await openDraft();

    openSettings("Voice", "Provider preferences");
    fireEvent.click(screen.getByLabelText("Voice enabled"));
    expect(screen.queryByTitle("primary-stt")).not.toBeInTheDocument();
    expect(screen.queryByTitle("primary-tts")).not.toBeInTheDocument();

    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Save draft" })); });
    await waitFor(() => expect(updateAdminDefinitionDraft).toHaveBeenCalled());
    const saved = vi.mocked(updateAdminDefinitionDraft).mock.calls.at(-1)?.[2] as {
      voice?: { enabled?: boolean };
      providerPreferences?: { speechRecognizer?: string | null; speechSynthesizer?: string | null };
    };
    expect(saved.voice?.enabled).toBe(false);
    expect(saved.providerPreferences).toMatchObject({
      speechRecognizer: null,
      speechSynthesizer: null,
      languageModel: "primary-llm"
    });
  });

  it("round-trips form and JSON without dropping untouched fields", async () => {
    await renderCandidate();

    fireEvent.click(screen.getByRole("tab", { name: "Profile" }));
    setText("Definition name", "Guide");
    fireEvent.click(screen.getByRole("radio", { name: "Advanced JSON" }));
    const json = screen.getByRole("textbox", { name: "Advanced JSON" }) as HTMLTextAreaElement;
    const edited = JSON.parse(json.value) as {
      identity: { name: string; role: string; tone: string };
      voice: { voiceId: string };
      metadata: { owner: string };
      modelDefaults: { catalogKey: string };
    };
    expect(edited.identity.name).toBe("Guide");
    expect(edited.voice.voiceId).toBe("verse");
    expect(edited.metadata.owner).toBe("kept");
    edited.identity.role = "Coach";
    fireEvent.change(json, { target: { value: JSON.stringify(edited, null, 2) } });
    fireEvent.click(screen.getByRole("radio", { name: "Form" }));
    openSettings();

    expect(screen.getByLabelText("Definition name")).toHaveValue("Guide");
    expect(screen.getByLabelText("Definition role")).toHaveValue("Coach");
    expect(screen.getByLabelText("Definition tone")).toHaveValue("Calm");
    openSettings("Voice");
    expect(screen.getByLabelText("Voice id")).toHaveValue("verse");
    expect(screen.getByTitle("Scripted Alpha")).toBeInTheDocument();
  });

  it("keeps dirty edits across views and does not save invalid JSON", async () => {
    mockDraft();
    await openDraft();
    // Cache the visible action/nav controls rather than rescanning retained form trees.
    const editor = document.querySelector(".admin-draft-editor")!;
    const actions = within(editor.querySelector(".admin-draft-actions")! as HTMLElement);
    const views = within(editor.querySelector(".admin-draft-view-switch")! as HTMLElement);
    const tabs = within(editor.querySelector(".admin-draft-tabs > .ant-tabs-nav")! as HTMLElement);
    const jsonView = views.getByRole("radio", { name: "Advanced JSON" });
    const formView = views.getByRole("radio", { name: "Form" });
    const save = actions.getByRole("button", { name: "Save draft" });
    const publish = actions.getByRole("button", { name: "Publish…" });
    const capabilities = tabs.getByRole("tab", { name: "Capabilities" });
    const resources = tabs.getByRole("tab", { name: "Skills & resources" });
    const identity = tabs.getByRole("tab", { name: "Identity & version" });

    setText("System instructions", "Unsaved instruction edit");
    fireEvent.click(jsonView);
    const json = screen.getByLabelText("Advanced JSON", { selector: "textarea" }) as HTMLTextAreaElement;
    expect(screen.getByText("Unsaved changes — save before using Test & Publish.")).toBeInTheDocument();
    expect(json.value).toContain(
      "Unsaved instruction edit"
    );

    fireEvent.change(json, { target: { value: "{ " } });
    expect(screen.getByText("Advanced JSON is invalid")).toBeInTheDocument();
    expect(save).toBeDisabled();
    await act(async () => { fireEvent.click(save); });
    fireEvent.click(publish);
    expect(updateAdminDefinitionDraft).not.toHaveBeenCalled();
    expect(publishAdminDefinitionDraft).not.toHaveBeenCalled();

    fireEvent.click(formView);
    expect(screen.getByLabelText("Advanced JSON", { selector: "textarea" })).toBeInTheDocument();
    // Visited tab content can remain mounted. Check the labeled field's visibility
    // directly instead of scanning every cached control's accessible role.
    const instructions = screen.queryByLabelText("System instructions", { selector: "textarea" });
    if (instructions) expect(instructions).not.toBeVisible();

    await act(async () => { fireEvent.click(capabilities); });
    expect(screen.getByText(/Fix Advanced JSON on Identity & version before changing capabilities/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Add knowledge source" })).toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: "Add knowledge source" }));

    await act(async () => { fireEvent.click(resources); });
    await act(async () => { fireEvent.click(screen.getByRole("tab", { name: "Resources" })); });
    await act(async () => { fireEvent.click(identity); });
    expect(screen.getByLabelText("Advanced JSON", { selector: "textarea" })).toHaveValue("{ ");
    expect(save).toBeInTheDocument();
    expect(save).toBeDisabled();
    expect(updateAdminDefinitionDraft).not.toHaveBeenCalled();
  });

  it("permits more than 32 authorized tools without a replacement count ceiling", async () => {
    const tools = Array.from({ length: 40 }, (_, i) => `workspace.tool${i}`);
    mockDraft({ ...storedCandidate, environment: { ...storedCandidate.environment, toolAllowlist: tools.slice(0, 33) } });
    vi.mocked(getAdminToolRegistry).mockResolvedValue({ toolNames: tools, maxToolAllowlistEntries: null });
    await openDraft();
    setText("System instructions", "Keep this edit");
    await act(async () => { fireEvent.click(screen.getByRole("tab", { name: "Capabilities" })); });
    expect(await screen.findByText("Authorized: 33 · Always selected: 33")).toBeInTheDocument();
    fireEvent.mouseDown(screen.getByLabelText("Authorized capabilities"));
    fireEvent.change(screen.getByLabelText("Authorized capabilities"), { target: { value: tools[33] } });
    await act(async () => { fireEvent.click(await screen.findByTitle(tools[33])); });
    expect(screen.getByText("Authorized: 34 · Always selected: 33")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Save draft" })).toBeEnabled();
  });

  it("preserves capability authorization and always projection through Form save", async () => {
    mockDraft({ ...storedCandidate, environment: { ...storedCandidate.environment, toolAllowlist: undefined,
      capabilities: { mode: "Selected", resolvedCapabilities: ["workspace.read", "knowledge.retrieve", "browser.navigate"] },
      projection: { alwaysCapabilities: ["workspace.read"] } } });
    vi.mocked(getAdminToolRegistry).mockResolvedValue({ toolNames: ["workspace.read", "knowledge.retrieve", "browser.navigate"], maxToolAllowlistEntries: null });
    await openDraft();
    setText("System instructions", "Keep this edit");
    await act(async () => { fireEvent.click(screen.getByRole("tab", { name: "Capabilities" })); });
    expect(await screen.findByText("Authorized: 3 · Always selected: 1")).toBeInTheDocument();
    expect(screen.getByLabelText("Always projected capabilities")).toBeInTheDocument();
    expect(screen.getByText("Core also includes authorized, eligible Browser v2 bootstrap tools and active Skill requirements. These can appear without an Always selection; permission still comes from Authorized capabilities.")).toBeVisible();
    expect(screen.queryByRole("button", { name: "Other authorized capabilities" })).not.toBeInTheDocument();
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Save draft" })); });
    expect(updateAdminDefinitionDraft).toHaveBeenCalledWith(draftId, 2, expect.objectContaining({ environment: expect.objectContaining({
      capabilities: { mode: "Selected", resolvedCapabilities: ["workspace.read", "knowledge.retrieve", "browser.navigate"] },
      projection: { alwaysCapabilities: ["workspace.read"] }
    }) }));
  });

  it("saves from JSON using the same draft revision", async () => {
    mockDraft();
    await openDraft();
    fireEvent.click(screen.getByRole("radio", { name: "Advanced JSON" }));
    const json = screen.getByRole("textbox", { name: "Advanced JSON" }) as HTMLTextAreaElement;
    const edited = JSON.parse(json.value) as { systemInstructions: string; untouchedMarker: { nested: string } };
    edited.systemInstructions = "Saved from JSON";
    fireEvent.change(json, { target: { value: JSON.stringify(edited, null, 2) } });
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Save draft" })); });
    await waitFor(() => {
      expect(updateAdminDefinitionDraft).toHaveBeenCalledWith(
        draftId,
        2,
        expect.objectContaining({
          systemInstructions: "Saved from JSON",
          untouchedMarker: { nested: "preserve-me" }
        })
      );
    });
  });
});

describe("authoring options failure", () => {
  function renderEditor() {
    render(
      <DefinitionCandidateEditor
        candidate={storedCandidate}
        view="form"
        jsonText="{}"
        busy={false}
        onCandidateChange={() => undefined}
        onJsonTextChange={() => undefined}
        onViewChange={() => undefined}
      />
    );
  }

  it("shows error details on the fixed warning when the response includes an id", async () => {
    const failure = new Error("Admin authoring options failed (500)");
    failure.name = "AdminRequestError";
    Object.assign(failure, { diagnosticId: "019944af-0008-7000-8000-0000000000e2" });
    vi.mocked(listAdminAuthoringOptions).mockRejectedValueOnce(failure);

    renderEditor();
    fireEvent.click(screen.getByRole("tab", { name: "Settings" }));

    await waitFor(() => {
      expect(screen.getByText(/Authoring options could not be loaded/)).toBeInTheDocument();
    });
    expect(screen.getByRole("button", { name: "Error details" })).toBeInTheDocument();
  });

  it("keeps the warning without error details when the failure has no id", async () => {
    vi.mocked(listAdminAuthoringOptions).mockRejectedValueOnce(new Error("offline"));

    renderEditor();
    fireEvent.click(screen.getByRole("tab", { name: "Settings" }));

    await waitFor(() => {
      expect(screen.getByText(/Authoring options could not be loaded/)).toBeInTheDocument();
    });
    expect(screen.queryByRole("button", { name: "Error details" })).not.toBeInTheDocument();
  });
});
