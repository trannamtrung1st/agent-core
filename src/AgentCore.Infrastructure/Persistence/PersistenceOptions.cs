namespace AgentCore.Infrastructure.Persistence;

public sealed class PersistenceOptions
{
    public string Provider { get; set; } = "InMemory";
    public string ConnectionString { get; set; } = "Data Source=data/agent-core.db";
    public int CheckpointMs { get; set; } = 1000;
    public int BusyTimeoutMs { get; set; } = 5000;
    public string AttachmentRoot { get; set; } = "data/attachments";
    public string WorkspaceRoot { get; set; } = "data/workspaces";
    public string TemplateRoot { get; set; } = "agents/templates";
    public string ArtifactRoot { get; set; } = "data/artifacts";
    public string DefinitionResourceRoot { get; set; } = "data/definition-resources";
}
