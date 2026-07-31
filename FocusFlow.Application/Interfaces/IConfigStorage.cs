using FocusFlow.Domain.Models;

namespace FocusFlow.Application.Interfaces;

/// <summary>
/// Persistence for <see cref="TimerConfig"/>.
/// </summary>
/// <remarks>
/// The interface lives in the Application layer rather than alongside its JSON
/// implementation in Infrastructure, so application services can depend on it without
/// the dependency arrow pointing outwards.
/// </remarks>
public interface IConfigStorage
{
    TimerConfig Load();
    void Save(TimerConfig config);
}
