namespace Milligram.Adapters.Companion;

/// <summary>Bounded terminal bytes whose replay begins between UTF-8 characters and escape sequences, under the host's output lock.</summary>
public sealed class TerminalReplay
{
    public const int DefaultCapacity = 1024 * 1024;
    private readonly byte[] bytes;
    private readonly bool[] boundaryAfter;
    private int start;
    private int count;
    private bool safeStart = true;
    private State state;
    private bool osc;
    private int continuation;
    private int scalar;

    public TerminalReplay(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        bytes = new byte[capacity];
        boundaryAfter = new bool[capacity];
    }

    public bool IsAtBoundary => state == State.Ground && continuation == 0;

    /// <summary>The first safe offset in this chunk for a client with no usable replay; its length means wait for later output.</summary>
    public int Append(ReadOnlySpan<byte> output)
    {
        var firstSafe = IsAtBoundary ? 0 : output.Length;
        var offset = 0;
        foreach (var value in output)
        {
            if (count == bytes.Length)
            {
                safeStart = boundaryAfter[start];
                start = (start + 1) % bytes.Length;
                count--;
            }
            var at = (start + count) % bytes.Length;
            bytes[at] = value;
            Advance(value);
            boundaryAfter[at] = IsAtBoundary;
            if (boundaryAfter[at] && firstSafe == output.Length) firstSafe = offset + 1;
            offset++;
            count++;
        }
        return firstSafe;
    }

    /// <summary>The tail can end mid-sequence: the client's decoder continues with subsequent live bytes.</summary>
    public byte[] Snapshot()
    {
        var skip = 0;
        var safe = safeStart;
        while (!safe && skip < count) safe = boundaryAfter[(start + skip++) % bytes.Length];
        var result = new byte[count - skip];
        for (var i = 0; i < result.Length; i++) result[i] = bytes[(start + skip + i) % bytes.Length];
        return result;
    }

    private void Advance(byte value)
    {
        if (continuation > 0)
        {
            if ((value & 0xC0) == 0x80)
            {
                scalar = (scalar << 6) | (value & 0x3F);
                if (--continuation == 0 && scalar is >= 0x80 and <= 0x9F) Control((byte)scalar);
                return;
            }
            continuation = 0;
        }
        if (value is >= 0xC2 and <= 0xF4)
        {
            if (state == State.StringEscape) state = State.String;
            continuation = value < 0xE0 ? 1 : value < 0xF0 ? 2 : 3;
            scalar = value & (continuation == 1 ? 0x1F : continuation == 2 ? 0x0F : 0x07);
            return;
        }
        Control(value);
    }

    private void Control(byte value)
    {
        if (value is 0x18 or 0x1A) { state = State.Ground; return; }
        if (state is State.String or State.StringEscape)
        {
            if (value == 0x9C || osc && value == 7 || state == State.StringEscape && value == '\\') state = State.Ground;
            else state = value == 0x1B ? State.StringEscape : State.String;
            return;
        }
        if (value == 0x1B) { state = State.Escape; return; }
        if (value == 0x9B) { state = State.Csi; return; }
        if (value is 0x90 or 0x98 or 0x9D or 0x9E or 0x9F)
        {
            osc = value == 0x9D;
            state = State.String;
            return;
        }
        switch (state)
        {
            case State.Escape:
                if (value == '[') state = State.Csi;
                else if (value is (byte)']' or (byte)'P' or (byte)'X' or (byte)'^' or (byte)'_')
                {
                    osc = value == ']';
                    state = State.String;
                }
                else if (value is >= 0x20 and <= 0x2F) state = State.Intermediate;
                else if (value is >= 0x30 and <= 0x7E) state = State.Ground;
                break;
            case State.Intermediate when value is >= 0x30 and <= 0x7E:
            case State.Csi when value is >= 0x40 and <= 0x7E:
                state = State.Ground;
                break;
        }
    }

    private enum State { Ground, Escape, Intermediate, Csi, String, StringEscape }
}
