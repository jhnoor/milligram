using System.Text;

namespace Milligram.Domain.Boards;

/// <summary>
/// A picture the agent draws to explain something: boxes it chose, linked to code or standing for something
/// with no code, notes, and labelled arrows. Layout is left to the viewer, so a board carries no coordinates.
/// </summary>
public sealed record Board
{
    public const int MaxBoxes = 200;
    public const int MaxArrows = 400;
    private const int MaxIdLength = 64;

    public string? Id { get; init; }
    public string Title { get; init; } = "";
    public IReadOnlyList<BoardBox> Boxes { get; init; } = [];
    public IReadOnlyList<BoardArrow> Arrows { get; init; } = [];

    /// <summary>Ids become file names, so only lowercase letters, digits and inner hyphens are allowed.</summary>
    public static bool IsValidId(string? id) =>
        id is { Length: > 0 and <= MaxIdLength } && id[0] != '-' && id[^1] != '-' &&
        id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    /// <summary>"How an order is priced" becomes "how-an-order-is-priced".</summary>
    public static string Slug(string title)
    {
        var slug = new StringBuilder();
        foreach (var c in title.ToLowerInvariant())
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9')) slug.Append(c);
            else if (slug.Length > 0 && slug[^1] != '-') slug.Append('-');
            if (slug.Length == MaxIdLength) break;
        }
        return slug.ToString().Trim('-');
    }

    /// <summary>The id the board is stored under: its own, or one made from its title.</summary>
    public string IdOrSlug() => Id is { Length: > 0 } id ? id : Slug(Title);

    /// <summary>Everything that stops the board from being drawn, in the order the agent should fix it.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (!IsValidId(IdOrSlug()))
            problems.Add(Id is { Length: > 0 }
                ? $"id \"{Id}\" must be 1-{MaxIdLength} lowercase letters, digits or inner hyphens."
                : "Give the board a title or an id.");
        if (Boxes.Count == 0) problems.Add("A board needs at least one box.");
        if (Boxes.Count > MaxBoxes) problems.Add($"A board can have at most {MaxBoxes} boxes.");
        if (Arrows.Count > MaxArrows) problems.Add($"A board can have at most {MaxArrows} arrows.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Boxes.Count; i++)
        {
            var box = Boxes[i];
            var name = box.Id is { Length: > 0 } ? $"Box \"{box.Id}\"" : $"Box {i + 1}";
            if (box.Id is not { Length: > 0 }) problems.Add($"{name} needs an id.");
            else if (!ids.Add(box.Id)) problems.Add($"{name} appears more than once.");
            var kinds = (Has(box.Ref) ? 1 : 0) + (Has(box.Note) ? 1 : 0);
            if (kinds == 0 && !Has(box.Label)) problems.Add($"{name} needs a ref, a label or a note.");
            if (kinds == 2) problems.Add($"{name} is either code (ref) or a note, not both.");
        }
        for (var i = 0; i < Arrows.Count; i++)
        {
            var arrow = Arrows[i];
            foreach (var end in new[] { arrow.From, arrow.To })
                if (!ids.Contains(end ?? "")) problems.Add($"Arrow {i + 1} points at \"{end}\", which is not a box id.");
        }
        return problems;
    }

    private static bool Has(string? value) => !string.IsNullOrWhiteSpace(value);
}

/// <summary>Refers to code (<see cref="Ref"/>), stands for something with no code (<see cref="Label"/>), or is a note.</summary>
public sealed record BoardBox
{
    public string? Id { get; init; }

    /// <summary>A type or member id from the model; <see cref="Label"/> then only renames the box.</summary>
    public string? Ref { get; init; }
    public string? Label { get; init; }
    public string? Note { get; init; }
}

public sealed record BoardArrow
{
    public string? From { get; init; }
    public string? To { get; init; }
    public string? Label { get; init; }
}
