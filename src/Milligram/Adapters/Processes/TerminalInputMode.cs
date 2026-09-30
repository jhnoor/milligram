namespace Milligram.Adapters.Processes;

/// <summary>Keep the local console in VT byte mode; a nested ConPTY cannot change the attachment's key protocol.</summary>
internal sealed class TerminalInputMode
{
    private const string Request = "\u001b[?9001h";
    private int matched;

    public char[] Feed(ReadOnlySpan<char> text)
    {
        var output = new List<char>(text.Length + matched);
        foreach (var value in text)
        {
            if (value == Request[matched])
            {
                if (++matched != Request.Length) continue;
                output.AddRange(Request[..^1]);
                output.Add('l');
                matched = 0;
                continue;
            }
            if (matched > 0) output.AddRange(Request[..matched]);
            matched = value == Request[0] ? 1 : 0;
            if (matched == 0) output.Add(value);
        }
        return output.ToArray();
    }
}
