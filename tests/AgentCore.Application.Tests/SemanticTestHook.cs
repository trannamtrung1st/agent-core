using System.Runtime.CompilerServices;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Infrastructure.Providers.SemanticResponses;

namespace AgentCore.Application.Tests;

internal static class SemanticTestHook
{
    internal static readonly AsyncLocal<bool> Bypass = new();

    [ModuleInitializer]
    internal static void Register()
    {
        SessionRuntime.TestDecorateLanguageModel = static model =>
            Bypass.Value || model is SemanticResponseLanguageModel
                ? model
                : new SemanticResponseLanguageModel(model);
    }
}
