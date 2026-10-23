using System.Text.Json;
using Milligram.Domain.Boards;

namespace Milligram.Application;

public sealed record BoardInfo(string Id, string Title, bool Kept, bool Draft);

/// <summary>
/// Where boards live. The agent draws on one pen file; drawing checks it and copies it into a throwaway draft,
/// which the next viewer start clears. Keep moves a draft into .milligram/boards/, where it survives restarts.
/// Ids are checked before they become file names.
/// </summary>
public sealed class BoardStore(ProjectPaths paths)
{
    /// <summary>Checks the board in <paramref name="file"/> (the pen by default) and stores it as a draft.</summary>
    public Board Draw(string? file = null)
    {
        var source = file ?? paths.BoardPenFile;
        Board? board;
        try { board = JsonFile.Read<Board>(source); }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new MilligramException($"{Name(source)}: {e.Message}");
        }
        if (board is null) throw new MilligramException($"Write the board to {Name(source)} first, then run `milligram tell board`.");
        if (board.Problems() is { Count: > 0 } problems)
            throw new MilligramException($"{Name(source)} can't be drawn:\n- {string.Join("\n- ", problems)}");
        board = board with { Id = board.IdOrSlug() };
        JsonFile.Write(Draft(board.Id!), board);
        return board;
    }

    /// <summary>Kept boards and drafts by id; a draft is the newer version of a kept board with the same id.</summary>
    public IReadOnlyList<BoardInfo> List()
    {
        var kept = Ids(paths.BoardsDirectory);
        var drafts = Ids(paths.BoardDraftsDirectory);
        return kept.Union(drafts).Order(StringComparer.Ordinal)
            .Select(id => (Id: id, Board: TryLoad(id)))
            .Where(x => x.Board is not null)
            .Select(x => new BoardInfo(x.Id, x.Board!.Title.Length > 0 ? x.Board.Title : x.Id, kept.Contains(x.Id), drafts.Contains(x.Id)))
            .ToList();
    }

    /// <summary>The draft if there is one, else the kept board; null when neither exists or the id is not a board id.</summary>
    public Board? Load(string id)
    {
        if (!Board.IsValidId(id)) return null;
        var file = File.Exists(Draft(id)) ? Draft(id) : Kept(id);
        try { return JsonFile.Read<Board>(file) is { } board ? board with { Id = id } : null; }
        catch (JsonException e) { throw new MilligramException($"{Name(file)}: {e.Message}"); }
    }

    /// <summary>Moves the draft over any kept board with the same id; false when there is no draft.</summary>
    public bool Keep(string id)
    {
        if (!Board.IsValidId(id) || !File.Exists(Draft(id))) return false;
        JsonFile.WriteText(Kept(id), JsonFile.ReadText(Draft(id)));
        File.Delete(Draft(id));
        return true;
    }

    /// <summary>Removes the board, draft and kept copy alike; false when there was nothing to remove.</summary>
    public bool Discard(string id)
    {
        if (!Board.IsValidId(id)) return false;
        var removed = false;
        foreach (var file in new[] { Draft(id), Kept(id) })
        {
            if (!File.Exists(file)) continue;
            File.Delete(file);
            removed = true;
        }
        return removed;
    }

    /// <summary>Drafts belong to one viewer session, so a new viewer starts without them.</summary>
    public void ClearDrafts()
    {
        if (!Directory.Exists(paths.BoardDraftsDirectory)) return;
        foreach (var file in Directory.EnumerateFiles(paths.BoardDraftsDirectory, "*.json")) File.Delete(file);
    }

    private Board? TryLoad(string id)
    {
        try { return Load(id); }
        catch (MilligramException) { return null; }
    }

    private static HashSet<string> Ids(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().Where(Board.IsValidId).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    private string Draft(string id) => Path.Combine(paths.BoardDraftsDirectory, id + ".json");
    private string Kept(string id) => Path.Combine(paths.BoardsDirectory, id + ".json");
    private string Name(string file) => paths.Contains(file) ? paths.Relative(file) : file;
}
