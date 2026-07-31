using System.Text.Json;
using System.Text.Json.Serialization;
using FocusFlow.Application.Interfaces;
using FocusFlow.Domain.Models;

namespace FocusFlow.Infrastructure.Storage;

/// <summary>
/// FR-013. Stores the in-flight <see cref="SessionState"/> as JSON next to the config.
/// </summary>
public sealed class JsonSessionStateStorage : ISessionStateStorage
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;

    public JsonSessionStateStorage(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolderOption.Create),
        "FocusFlow",
        "session.json");

    public SessionState? Load()
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SessionState>(File.ReadAllText(_filePath), Options);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(SessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Temp-then-move: this file is rewritten every few seconds, so it is the most
        // likely thing to be mid-write when the process dies — exactly the crash FR-013
        // is about.
        var tempPath = _filePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(state, Options));
        File.Move(tempPath, _filePath, overwrite: true);
    }

    public void Clear()
    {
        try
        {
            File.Delete(_filePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
