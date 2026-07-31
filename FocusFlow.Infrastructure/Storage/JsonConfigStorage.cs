using System.Text.Json;
using System.Text.Json.Serialization;
using FocusFlow.Application.Interfaces;
using FocusFlow.Domain.Models;

namespace FocusFlow.Infrastructure.Storage;

/// <summary>
/// Stores <see cref="TimerConfig"/> as JSON on disk.
/// </summary>
public sealed class JsonConfigStorage : IConfigStorage
{
    private static readonly JsonSerializerOptions Options = new()
    {
        // Indented because this file is somewhere a user might reasonably hand-edit.
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;

    public JsonConfigStorage(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    /// <summary>
    /// Builds the default per-user config path, e.g.
    /// %APPDATA%\FocusFlow\config.json or ~/.config/FocusFlow/config.json.
    /// </summary>
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolderOption.Create),
        "FocusFlow",
        "config.json");

    public TimerConfig Load()
    {
        if (!File.Exists(_filePath))
        {
            return new TimerConfig();
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<TimerConfig>(json, Options) ?? new TimerConfig();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // A truncated or hand-mangled file should cost the user their customisations,
            // not the ability to start the app.
            return new TimerConfig();
        }
    }

    public void Save(TimerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // On first run this directory does not exist yet; WriteAllText would throw
        // DirectoryNotFoundException rather than create it.
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write to a sibling temp file and move it into place, so a crash mid-write
        // cannot leave a half-written config behind.
        var tempPath = _filePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(config, Options));
        File.Move(tempPath, _filePath, overwrite: true);
    }
}
