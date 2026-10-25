using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantDefinitionAccessTests
{
    [Fact]
    public async Task Environment_HTTP_keeps_protected_values_write_only_and_profiles_explicitly_disabled()
    {
        await using var factory = new AgentCoreApiFactory();
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var root = $"/api/v2/tenants/{tenant}"; var secretPath = root + "/secrets";
        const string value = "HTTP_SECRET_SENTINEL_never_echoed";
        var createdSecret = await owner.PostAsJsonAsync(secretPath, new { secretId = Guid.NewGuid(), name = "Build token", description = "Protected", protectedValue = value, expectedRevision = await Revision(owner, tenant) });
        Assert.Equal(HttpStatusCode.Created, createdSecret.StatusCode); Assert.DoesNotContain(value, await createdSecret.Content.ReadAsStringAsync());
        var secret = (await Json(createdSecret)).GetProperty("secretId").GetGuid();
        Assert.DoesNotContain(value, await owner.GetStringAsync(secretPath));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await owner.GetAsync($"{secretPath}/{secret}/value")).StatusCode);
        var space = Guid.NewGuid();
        var createdSpace = await owner.PostAsJsonAsync(root + "/spaces", new { spaceId = space, name = "Build", description = "", instructions = "", expectedRevision = await Revision(owner, tenant) });
        Assert.Equal(HttpStatusCode.Created, createdSpace.StatusCode); Assert.False((await Json(createdSpace)).GetProperty("environmentsEnabled").GetBoolean());
        var env = Guid.NewGuid(); var envPath = $"{root}/spaces/{space}/environments";
        var createdEnv = await owner.PostAsJsonAsync(envPath, new { environmentId = env, name = "Development", description = "Saved profile", expectedRevision = await Revision(owner, tenant), expectedSpaceRevision = 1 });
        Assert.Equal(HttpStatusCode.Created, createdEnv.StatusCode);
        var variables = $"{envPath}/{env}/variables";
        var plain = await owner.PutAsJsonAsync(variables + "/MODE", new { kind = "Plain", description = "Model visible on demand", plainValue = "PLAIN_ONLY_ON_DEMAND", expectedRevision = await Revision(owner, tenant), expectedSpaceRevision = 1, expectedEnvironmentRevision = 1, expectedVariableRevision = 0 });
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode); Assert.DoesNotContain("PLAIN_ONLY_ON_DEMAND", await plain.Content.ReadAsStringAsync());
        var reference = await owner.PutAsJsonAsync(variables + "/TOKEN", new { kind = "SecretReference", description = "Protected", secretId = secret, expectedRevision = await Revision(owner, tenant), expectedSpaceRevision = 1, expectedEnvironmentRevision = 2, expectedVariableRevision = 0 });
        Assert.Equal(HttpStatusCode.OK, reference.StatusCode); Assert.DoesNotContain(value, await reference.Content.ReadAsStringAsync());
        var listing = await owner.GetStringAsync(variables); Assert.DoesNotContain(value, listing); Assert.DoesNotContain("PLAIN_ONLY_ON_DEMAND", listing);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.DeleteAsync($"{secretPath}/{secret}?expectedRevision={await Revision(owner, tenant)}&expectedSecretRevision=1")).StatusCode);
        var malformed = await owner.PutAsJsonAsync(variables + "/BROKEN", new { kind = "SecretReference", description = "", plainValue = "invalid", secretId = secret, expectedRevision = await Revision(owner, tenant), expectedSpaceRevision = 1, expectedEnvironmentRevision = 3, expectedVariableRevision = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"{envPath}/{env}?expectedRevision={await Revision(owner, tenant)}&expectedSpaceRevision=1&expectedEnvironmentRevision=3")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"{secretPath}/{secret}?expectedRevision={await Revision(owner, tenant)}&expectedSecretRevision=1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"{secretPath}/{secret}")).StatusCode);
    }
}
