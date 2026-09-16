namespace AgentCore.Application.Ports;

public interface IOwnerCapabilityStore
{
    ValueTask SaveHashAsync(string tokenHash, DateTimeOffset createdAt, CancellationToken cancellationToken = default);

    ValueTask<bool> ContainsHashAsync(string tokenHash, CancellationToken cancellationToken = default);
}

public interface IOwnerCapabilityService
{
    ValueTask<IssuedOwnerCapability> IssueAsync(CancellationToken cancellationToken = default);

    ValueTask<bool> ValidateAsync(string? token, CancellationToken cancellationToken = default);
}

public sealed record IssuedOwnerCapability(string Token, DateTimeOffset IssuedAt);
