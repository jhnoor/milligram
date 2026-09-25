namespace Milligram.Domain.Model;

public enum TypeKind { Class, Record, Struct, RecordStruct, Interface, Enum, Delegate }

public enum Visibility { Public, Internal, Protected, ProtectedInternal, PrivateProtected, Private }

public enum MemberKind
{
    Method, Constructor, Destructor, Property, Indexer, Event, Operator, Field, EnumValue, TopLevel, Initializer
}

/// <summary>Edge kinds in ascending strength; merging keeps the strongest.</summary>
public enum EdgeKind { Association, Dependency, Implements, Inheritance }

/// <summary>A region of a source file. Lines and columns are 1-based; Start/End are 0-based character offsets.</summary>
public sealed record SourceSpan(string File, int StartLine, int StartColumn, int EndLine, int EndColumn, int Start, int End)
{
    public bool Contains(int line, int column) =>
        (line > StartLine || (line == StartLine && column >= StartColumn)) &&
        (line < EndLine || (line == EndLine && column <= EndColumn));

    public bool ContainsLine(int line) => line >= StartLine && line <= EndLine;

    public int Length => End - Start;
}

public sealed record MemberNode(
    string Id,
    string Name,
    MemberKind Kind,
    string Signature,
    Visibility Visibility,
    bool IsStatic,
    bool IsAbstract,
    SourceSpan Span,
    int? Complexity,
    string Hash);

public sealed record TypeNode(
    string Id,
    string Name,
    string Namespace,
    TypeKind Kind,
    Visibility Visibility,
    bool IsAbstract,
    bool IsStatic,
    IReadOnlyList<SourceSpan> Spans,
    IReadOnlyList<MemberNode> Members)
{
    public string File => Spans.Count > 0 ? Spans[0].File : "";

    public IEnumerable<string> Files => Spans.Select(s => s.File).Distinct();
}

/// <summary>An external library shown as an oval, e.g. "Microsoft.CodeAnalysis".</summary>
public sealed record ForeignNode(string Id, string Label);

public sealed record DependencyEdge(string From, string To, EdgeKind Kind, int Count);

public sealed record CodeModel(
    string Title,
    string Prefix,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<TypeNode> Types,
    IReadOnlyList<ForeignNode> Foreign,
    IReadOnlyList<DependencyEdge> Edges)
{
    public static CodeModel Empty(string title, string prefix) =>
        new(title, prefix, DateTimeOffset.MinValue, [], [], []);

    public const string ForeignIdPrefix = "x:";
}
