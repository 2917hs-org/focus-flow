using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace FocusFlow.App.Services;

/// <summary>
/// FR-009/FR-010. Lets the user pick their own audio file for the alarm or music.
/// </summary>
public interface IFilePickerService
{
    Task<string?> PickAudioFileAsync(string title, IReadOnlyList<string> extensions);
}

public sealed class FilePickerService : IFilePickerService
{
    public async Task<string?> PickAudioFileAsync(string title, IReadOnlyList<string> extensions)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            return null;
        }

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Audio")
                {
                    // Patterns want "*.mp3"; the platform reports plain extensions.
                    Patterns = extensions.Select(e => "*" + e).ToArray()
                }
            ]
        });

        // TryGetLocalPath returns null for cloud/virtual items, which we cannot hand to
        // afplay or MCI anyway.
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
