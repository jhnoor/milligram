using Milligram.Domain.Policies;

namespace Milligram.Application;

/// <summary>Policy edits the viewer can make on its own: proposals and omissions.</summary>
public sealed class PolicyEditor(Workspace workspace)
{
    public Proposal NewProposal(string? name = null)
    {
        var now = DateTime.Now;
        var proposal = new Proposal
        {
            Id = UniqueId(now.ToString("'p'yyyyMMddHHmmss")),
            Name = string.IsNullOrWhiteSpace(name) ? now.ToString("yyyy-MM-dd HH:mm:ss") : name.Trim(),
        };
        workspace.EditPolicy(p => p with { Proposals = [.. p.Proposals, proposal] });
        return proposal;
    }

    public void RenameProposal(string id, string name) =>
        workspace.EditPolicy(p => p with
        {
            Proposals = p.Proposals.Select(x => x.Id == id ? x with { Name = name.Trim() } : x).ToList(),
        });

    public void DeleteProposal(string id) =>
        workspace.EditPolicy(p => p with { Proposals = p.Proposals.Where(x => x.Id != id).ToList() });

    /// <summary>Leaves <paramref name="target"/> off the proposal if one is shown, otherwise off the real diagram.</summary>
    public void Omit(string target, string? proposalId)
    {
        workspace.EditPolicy(p =>
        {
            if (proposalId is not null && p.FindProposal(proposalId) is { } proposal)
                return p with { Proposals = p.Proposals.Select(x => x == proposal ? x with { Omit = Add(x.Omit, target) } : x).ToList() };
            return p with { Omit = Add(p.Omit, target) };
        });
    }

    private static IReadOnlyList<string> Add(IReadOnlyList<string> list, string item) =>
        list.Contains(item) ? list : [.. list, item];

    private string UniqueId(string id)
    {
        var existing = workspace.Policy.Proposals.Select(p => p.Id).ToHashSet();
        var candidate = id;
        for (var n = 2; existing.Contains(candidate); n++) candidate = $"{id}-{n}";
        return candidate;
    }
}
