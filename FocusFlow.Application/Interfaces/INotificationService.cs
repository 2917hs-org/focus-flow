namespace FocusFlow.Application.Interfaces;

/// <summary>
/// Native OS notifications (FR-008). Sound is handled separately by
/// <see cref="IAudioPlayer"/>.
/// </summary>
public interface INotificationService
{
    void ShowNotification(string title, string message);
}
