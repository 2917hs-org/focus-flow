using FocusFlow.Domain.Models;

namespace FocusFlow.Application.Interfaces;

/// <summary>
/// FR-013. Persists the in-flight session so an unexpected exit doesn't lose it.
/// </summary>
public interface ISessionStateStorage
{
    /// <summary>Returns the saved session, or null when there is nothing to resume.</summary>
    SessionState? Load();

    void Save(SessionState state);

    /// <summary>Removes the saved session — the run finished normally.</summary>
    void Clear();
}
