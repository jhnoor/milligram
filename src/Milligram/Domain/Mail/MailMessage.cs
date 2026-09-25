using System.Text.Json.Nodes;

namespace Milligram.Domain.Mail;

/// <summary>
/// One message between the viewer and the companion agent. Each message is its own file in a mailbox
/// directory, so writers never overwrite each other and the reader deletes what it has handled.
/// </summary>
public sealed record MailMessage(string Id, string Op, DateTimeOffset At, JsonObject Data)
{
    public static class Ops
    {
        // viewer -> agent
        public const string Context = "context";
        public const string Message = "message";
        public const string Changed = "changed";

        // agent -> viewer
        public const string Display = "display";
        public const string Notify = "notify";
        public const string Reload = "reload";
    }

    /// <summary>Sortable, unique file name: time first so the oldest message is read first.</summary>
    public static string FileName(DateTimeOffset at, string unique, string op) =>
        $"{at.UtcDateTime:yyyyMMddTHHmmssfff}-{unique}-{Sanitize(op)}.json";

    private static string Sanitize(string op) =>
        new(op.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());
}
