using FocusFlow.Domain.Models;

namespace FocusFlow.Application.Interfaces;

/// <summary>
/// Append-only log of finished sessions, kept in a local file.
/// </summary>
/// <remarks>
/// Append-only rather than read-modify-write: a session is never edited after the fact,
/// appending cannot corrupt earlier records, and it keeps the cost of finishing a session
/// independent of how much history has accumulated.
/// </remarks>
public interface ISessionHistoryStore
{
    void Append(SessionRecord record);

    /// <summary>
    /// Reads the log, oldest first. <paramref name="since"/> filters by
    /// <see cref="SessionRecord.EndedAt"/>; null reads everything.
    /// </summary>
    IReadOnlyList<SessionRecord> Read(DateTimeOffset? since = null);
}
