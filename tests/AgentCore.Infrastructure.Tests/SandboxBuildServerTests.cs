using AgentCore.Infrastructure.Sandbox;

namespace AgentCore.Infrastructure.Tests;

public sealed class SandboxBuildServerTests
{
    [Theory]
    [InlineData("/usr/share/dotnet/sdk/10.0.402/Roslyn/bincore/VBCSCompiler\nVBCSCompiler\n-pipename:fixture\n", true)]
    [InlineData("/usr/share/dotnet/dotnet\ndotnet\n/usr/share/dotnet/sdk/10.0.402/Roslyn/bincore/VBCSCompiler.dll\n-pipename:fixture\n", true)]
    [InlineData("/usr/lib/dotnet/dotnet\ndotnet\n/usr/lib/dotnet/sdk/10.0.402/MSBuild.dll\n/nodemode:8\n", true)]
    [InlineData("/usr/lib/dotnet/dotnet\ndotnet\n/usr/lib/dotnet/sdk/10.0.402/MSBuild.dll\n/working/app.csproj\n", false)]
    [InlineData("/usr/share/dotnet/dotnet\ndotnet\n/working/Roslyn/bincore/VBCSCompiler.dll\n-pipename:fixture\n", false)]
    [InlineData("/working/sdk/10.0.402/Roslyn/bincore/VBCSCompiler\nVBCSCompiler\n-pipename:fixture\n", false)]
    [InlineData("/usr/share/dotnet/dotnet\ndotnet\n/usr/lib/dotnet/sdk/10.0.402/MSBuild.dll\n/nodemode:8\n", false)]
    [InlineData("/bin/sleep\nsleep\n120\n", false)]
    public void Only_recognized_sdk_build_servers_receive_managed_shutdown(string observation, bool recognized)
    {
        var result = OpenSandboxService.RecognizeBuildServer(observation);
        Assert.Equal(recognized, result is not null);
        if (result is { } server) Assert.EndsWith("/sdk/10.0.402/dotnet.dll", server.Cli);
    }
}
