namespace Milligram.Adapters.Companion;

/// <summary>Ctrl+] then d detaches; doubling Ctrl+] passes the prefix through to the agent.</summary>
internal sealed class TerminalDetach
{
    private const byte Prefix = 0x1d;
    private bool pending;

    public byte[] Feed(ReadOnlySpan<byte> input, out bool detached)
    {
        var output = new List<byte>(input.Length + 1);
        detached = false;
        foreach (var value in input)
        {
            if (pending)
            {
                pending = false;
                if (value is (byte)'d' or (byte)'D') { detached = true; break; }
                output.Add(Prefix);
                if (value == Prefix) continue;
            }
            if (value == Prefix) pending = true;
            else output.Add(value);
        }
        return output.ToArray();
    }
}
