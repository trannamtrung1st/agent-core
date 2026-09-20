using System.Runtime.CompilerServices;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Infrastructure.Providers.SemanticResponses;

namespace AgentCore.Application.Tests;

internal static class SemanticTestHook
{
    [ModuleInitializer]
    internal static void Register()
    {
        SessionRuntime.TestDecorateLanguageModel = static model =>
            model is SemanticResponseLanguageModel ? model : new SemanticResponseLanguageModel(model);
    }
}
