import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { AdminApp } from "./AdminApp";

vi.mock("../../services/adminApi", () => ({
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
  listAdminToolNames: vi.fn().mockResolvedValue(["workspace.read"]),
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
  listAdminDefinitionEvaluationResults: vi.fn()
}));

import {
  getAdminDefinitionDraft,
  listAdminDefinitionDrafts,
  listAdminDefinitionPublications,
  listAdminDefinitions,
  listAdminDraftResources,
  listAdminInstances,
  listAdminToolNames,
  publishAdminDefinitionDraft,
  updateAdminDefinitionDraft
} from "../../services/adminApi";

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
  vi.mocked(listAdminToolNames).mockResolvedValue(["workspace.read", "knowledge.retrieve"]);
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
  await waitFor(() => {
    expect(screen.getByRole("button", { name: /Draft rev 2/ })).toBeInTheDocument();
  });
  fireEvent.click(screen.getByRole("button", { name: /Draft rev 2/ }));
  await waitFor(() => {
    expect(screen.getByLabelText("System instructions")).toBeInTheDocument();
  });
}

async function chooseOption(label: string, optionName: string) {
  fireEvent.mouseDown(screen.getByRole("combobox", { name: label }));
  fireEvent.click(await screen.findByTitle(optionName));
}

function setSpin(label: string, value: string) {
  fireEvent.change(screen.getByRole("spinbutton", { name: label }), { target: { value } });
}

function setText(label: string, value: string) {
  fireEvent.change(screen.getByLabelText(label), { target: { value } });
}

describe("definition candidate editor", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("edits every supported candidate area through the form and saves that candidate", async () => {
    mockDraft();
    await openDraft();

    expect(screen.getByLabelText("Definition ID")).toHaveTextContent("examiner");
    expect(screen.queryByRole("textbox", { name: "Definition ID" })).not.toBeInTheDocument();
    expect(screen.getByRole("spinbutton", { name: "Silence threshold" })).toHaveAccessibleDescription(
      "Milliseconds. 1,000 to 120,000."
    );
    expect(screen.getByRole("spinbutton", { name: "Cooldown" })).toHaveAccessibleDescription(
      "Milliseconds. 5,000 to 600,000."
    );
    expect(screen.getByRole("spinbutton", { name: "Max inactivity" })).toHaveAccessibleDescription(
      "Milliseconds. Empty uses 900,000."
    );
    expect(screen.getByRole("spinbutton", { name: "Silence threshold" })).toHaveValue("3000");
    expect(screen.getByLabelText("Conversation language")).toHaveAccessibleDescription(
      "auto, or a BCP 47 tag such as en."
    );
    expect(screen.getByLabelText("Model catalog key")).toHaveValue("scripted-alpha");
    expect(screen.getByText(/Speech aliases are required when voice is on/)).toBeInTheDocument();

    setText("Definition name", "Guide");
    setText("Definition role", "Coach");
    setText("Definition description", "Helps the operator.");
    setText("Definition tone", "Direct");
    setText("Goal 1", "Coach the operator");
    fireEvent.click(screen.getByRole("button", { name: "Add goal" }));
    setText("Goal 2", "Keep answers short");
    setText("System instructions", "Edited instructions");
    await chooseOption("Interruption style", "Answer the new turn");
    fireEvent.click(screen.getByRole("switch", { name: "Acknowledge interruption" }));
    fireEvent.click(screen.getByRole("switch", { name: "Avoid unsupported claims" }));
    await chooseOption("Response length", "Balanced");
    setText("Conversation language", "en");
    setSpin("Max output tokens", "512");
    fireEvent.click(screen.getByRole("switch", { name: "Ask one question at a time" }));
    fireEvent.click(screen.getByRole("switch", { name: "Initiative enabled" }));
    setSpin("Silence threshold", "4000");
    setSpin("Cooldown", "20000");
    setSpin("Max prompts per silence", "2");
    setSpin("Max consecutive proactive turns", "3");
    setSpin("Max silent evaluations", "9");
    setSpin("Max inactivity", "120000");
    await chooseOption("Initiative triggers", "Environment update");
    setText("Model catalog key", "scripted-beta");
    setText("Reasoning effort", "medium");
    setText("Language model", "backup-llm");
    setText("Speech recognizer", "primary-stt");
    setText("Speech synthesizer", "primary-tts");
    setText("Interruption classifier", "manual");
    fireEvent.click(screen.getByRole("switch", { name: "Voice enabled" }));
    setText("Voice id", "alloy");
    setSpin("Speaking rate", "1.2");
    fireEvent.click(screen.getByRole("switch", { name: "Session memory" }));
    fireEvent.click(screen.getByRole("switch", { name: "Identity user promotion" }));
    fireEvent.click(screen.getByRole("switch", { name: "Identity user retrieval" }));
    fireEvent.click(screen.getByRole("switch", { name: "User promotion" }));
    fireEvent.click(screen.getByRole("switch", { name: "User retrieval" }));
    fireEvent.click(screen.getByRole("switch", { name: "Automation enabled" }));
    fireEvent.click(screen.getByRole("switch", { name: "Allow user scheduling" }));
    fireEvent.click(screen.getByRole("switch", { name: "Allow one-shot" }));
    fireEvent.click(screen.getByRole("switch", { name: "Allow daily" }));
    fireEvent.click(screen.getByRole("switch", { name: "Allow weekly" }));
    fireEvent.click(screen.getByRole("switch", { name: "Allow indefinite recurrence" }));
    fireEvent.click(screen.getByRole("switch", { name: "Allow fixed interval" }));
    setSpin("Max active registrations", "8");
    setSpin("One-shot horizon days", "14");
    setSpin("Minimum recurrence days", "2");
    setSpin("Minimum fixed interval seconds", "120");
    await chooseOption("Allowed source kinds", "Application event");
    setText("Metadata key 1", "team");
    setText("Metadata value 1", "platform");

    fireEvent.click(screen.getByRole("tab", { name: "Capabilities" }));
    const harness = screen.getByLabelText("Harness labels");
    fireEvent.mouseDown(harness);
    fireEvent.change(harness, { target: { value: "lab-harness" } });
    fireEvent.keyDown(harness, { key: "Enter", code: "Enter", keyCode: 13 });
    await chooseOption("Tool allowlist", "workspace.read");
    fireEvent.click(screen.getByRole("button", { name: "Add knowledge source" }));
    setText("Knowledge identity 1", "refund-policy");
    setText("Knowledge title 1", "Refund Policy");
    setText("Knowledge citation 1", "refund-policy@v3");
    setText("Workspace template id", "examiner-default");
    fireEvent.click(screen.getByRole("switch", { name: "Allow unread unsupported attachment types" }));

    fireEvent.click(screen.getByRole("tab", { name: "Definition" }));
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
        toolAllowlist: string[];
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
    expect(parsed.modelDefaults).toEqual({ catalogKey: "scripted-beta", reasoningEffort: "medium" });
    expect(parsed.providerPreferences).toEqual({
      languageModel: "backup-llm",
      speechRecognizer: "primary-stt",
      speechSynthesizer: "primary-tts",
      interruptionClassifier: "manual"
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
    expect(parsed.environment.toolAllowlist).toEqual(["workspace.read"]);
    expect(parsed.environment.knowledgeSources).toEqual([
      { identity: "refund-policy", title: "Refund Policy", citation: "refund-policy@v3" }
    ]);
    expect(parsed.environment.workspace.templateId).toBe("examiner-default");
    expect(parsed.environment.attachments.allowUnreadUnsupportedTypes).toBe(true);
    expect(parsed.untouchedMarker).toEqual({ nested: "preserve-me" });

    fireEvent.click(screen.getByRole("button", { name: "Save draft" }));
    await waitFor(() => {
      expect(updateAdminDefinitionDraft).toHaveBeenCalledWith(draftId, 2, expect.any(Object));
    });
    expect(vi.mocked(updateAdminDefinitionDraft).mock.calls.at(-1)?.[2]).toEqual(parsed);
    expect(publishAdminDefinitionDraft).not.toHaveBeenCalled();
    // Walking every field re-renders the Admin shell. A local run took about 46s.
    // Hosted run 36424818606 killed this test at the previous 60s budget.
  }, 180_000);

  it("saves a voice-enabled draft with default speech aliases", async () => {
    mockDraft();
    await openDraft();

    fireEvent.click(screen.getByRole("switch", { name: "Voice enabled" }));
    expect(screen.getByLabelText("Speech recognizer")).toHaveValue("primary-stt");
    expect(screen.getByLabelText("Speech synthesizer")).toHaveValue("primary-tts");

    fireEvent.click(screen.getByRole("button", { name: "Save draft" }));
    await waitFor(() => {
      expect(updateAdminDefinitionDraft).toHaveBeenCalled();
    });
    const saved = vi.mocked(updateAdminDefinitionDraft).mock.calls.at(-1)?.[2] as {
      voice?: { enabled?: boolean };
      providerPreferences?: { speechRecognizer?: string; speechSynthesizer?: string };
    };
    expect(saved.voice?.enabled).toBe(true);
    expect(saved.providerPreferences).toMatchObject({
      speechRecognizer: "primary-stt",
      speechSynthesizer: "primary-tts"
    });
  });

  it("clears speech aliases when voice is turned off so the draft can be saved", async () => {
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

    fireEvent.click(screen.getByRole("switch", { name: "Voice enabled" }));
    expect(screen.getByLabelText("Speech recognizer")).toHaveValue("");
    expect(screen.getByLabelText("Speech synthesizer")).toHaveValue("");

    fireEvent.click(screen.getByRole("button", { name: "Save draft" }));
    await waitFor(() => {
      expect(updateAdminDefinitionDraft).toHaveBeenCalled();
    });
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
    mockDraft();
    await openDraft();

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

    expect(screen.getByLabelText("Definition name")).toHaveValue("Guide");
    expect(screen.getByLabelText("Definition role")).toHaveValue("Coach");
    expect(screen.getByLabelText("Definition tone")).toHaveValue("Calm");
    expect(screen.getByLabelText("Voice id")).toHaveValue("verse");
    expect(screen.getByLabelText("Model catalog key")).toHaveValue("scripted-alpha");
    expect(screen.getByText("Unsaved changes — save before using Test & Publish.")).toBeInTheDocument();
  });

  it("keeps dirty edits across views and does not save invalid JSON", async () => {
    mockDraft();
    await openDraft();

    setText("System instructions", "Unsaved instruction edit");
    fireEvent.click(screen.getByRole("radio", { name: "Advanced JSON" }));
    expect(screen.getByText("Unsaved changes — save before using Test & Publish.")).toBeInTheDocument();
    expect((screen.getByRole("textbox", { name: "Advanced JSON" }) as HTMLTextAreaElement).value).toContain(
      "Unsaved instruction edit"
    );

    fireEvent.change(screen.getByRole("textbox", { name: "Advanced JSON" }), { target: { value: "{ " } });
    expect(screen.getByText("Advanced JSON is invalid")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Save draft" })).toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: "Save draft" }));
    fireEvent.click(screen.getByRole("button", { name: "Publish…" }));
    expect(updateAdminDefinitionDraft).not.toHaveBeenCalled();
    expect(publishAdminDefinitionDraft).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("radio", { name: "Form" }));
    expect(screen.getByRole("textbox", { name: "Advanced JSON" })).toBeInTheDocument();
    expect(screen.queryByRole("textbox", { name: "System instructions" })).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("tab", { name: "Capabilities" }));
    expect(screen.getByText(/Fix Advanced JSON on the Definition tab before changing capabilities/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Add knowledge source" })).toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: "Add knowledge source" }));

    fireEvent.click(screen.getByRole("tab", { name: "Resources" }));
    fireEvent.click(screen.getByRole("tab", { name: "Definition" }));
    expect(screen.getByRole("textbox", { name: "Advanced JSON" })).toHaveValue("{ ");
    expect(screen.getByRole("button", { name: "Save draft" })).toBeDisabled();
    expect(updateAdminDefinitionDraft).not.toHaveBeenCalled();
  });

  it("saves from JSON using the same draft revision", async () => {
    mockDraft();
    await openDraft();
    fireEvent.click(screen.getByRole("radio", { name: "Advanced JSON" }));
    const json = screen.getByRole("textbox", { name: "Advanced JSON" }) as HTMLTextAreaElement;
    const edited = JSON.parse(json.value) as { systemInstructions: string; untouchedMarker: { nested: string } };
    edited.systemInstructions = "Saved from JSON";
    fireEvent.change(json, { target: { value: JSON.stringify(edited, null, 2) } });
    fireEvent.click(screen.getByRole("button", { name: "Save draft" }));
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
