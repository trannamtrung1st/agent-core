namespace AgentCore.Application.Speech;

public sealed class ResponseTextAccumulator
{
    public string Text { get; private set; } = string.Empty;

    public int Length => Text.Length;

    public (int Start, string Delta) Append(string delta)
    {
        var start = Text.Length;
        Text += delta;
        return (start, delta);
    }

    public void Reset() => Text = string.Empty;
}
