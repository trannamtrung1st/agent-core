namespace AgentCore.Application.Tools;

public readonly record struct ProductPublishStep(string Tool, string Detail);

public static class ProductPublishVerification
{
    public static bool IsStorefrontVerified(IReadOnlyList<ProductPublishStep> steps)
    {
        var lastAct = -1;
        for (var index = 0; index < steps.Count; index++)
        {
            if (string.Equals(steps[index].Tool, ToolCatalog.BrowserAct, StringComparison.Ordinal))
            {
                lastAct = index;
            }
        }

        if (lastAct < 0)
        {
            return false;
        }

        for (var index = lastAct + 1; index < steps.Count; index++)
        {
            var step = steps[index];
            if (step.Tool is not (ToolCatalog.BrowserObserve or ToolCatalog.BrowserNavigate))
            {
                continue;
            }

            if (step.Detail.Contains("AC Keyboard", StringComparison.Ordinal)
                && step.Detail.Contains("$99", StringComparison.Ordinal)
                && step.Detail.Contains("Published", StringComparison.Ordinal)
                && step.Detail.Contains("ac-keyboard.png", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
