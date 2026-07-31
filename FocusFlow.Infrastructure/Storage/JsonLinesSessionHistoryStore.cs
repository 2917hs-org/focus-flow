using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FocusFlow.Application.Interfaces;
using FocusFlow.Domain.Models;

namespace FocusFlow.Infrastructure.Storage;

/// <summary>
/// Session history as JSON Lines — one self-contained JSON object per line.
/// </summary>
/// <remarks>
/// <para>
/// JSONL rather than one big JSON array, for three reasons that all matter to a log that
/// is written for years and read by future reporting: appending is O(1) and never rewrites
/// existing records; a process killed mid-write can only damage the final line, which is
/// skipped on read rather than taking the whole file with it; and any reporting tool,
/// including plain command-line ones, can stream it a line at a time.
/// </para>
/// <para>
/// Written as UTF-8 with invariant round-trip formatting, so the file means the same thing
/// on every machine and under every locale.
/// </para>
/// </remarks>
public sealed class JsonLinesSessionHistoryStore : ISessionHistoryStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        // One line per record: indentation would break the format.
        WriteIndented = false,
        // Names, not ordinals. This file is written to be read later by reporting code
        // and by people: "Study" survives an enum member being added or removed, whereas
        // a bare 1 silently re-points at whatever now sits in that slot.
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;
    private readonly Lock _gate = new();

    public JsonLinesSessionHistoryStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolderOption.Create),
        "FocusFlow",
        "history.jsonl");

    public void Append(SessionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var line = JsonSerializer.Serialize(record, Options);

        lock (_gate)
        {
            File.AppendAllText(_filePath, line + Environment.NewLine, Encoding.UTF8);
        }
    }

    public IReadOnlyList<SessionRecord> Read(DateTimeOffset? since = null)
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        var records = new List<SessionRecord>();

        lock (_gate)
        {
            foreach (var line in File.ReadLines(_filePath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                SessionRecord? record;
                try
                {
                    record = JsonSerializer.Deserialize<SessionRecord>(line, Options);
                }
                catch (JsonException)
                {
                    // A torn final line from a killed process, or a record written by a
                    // newer version. Skip it rather than lose the rest of the history.
                    continue;
                }

                if (record is not null && (since is null || record.EndedAt >= since))
                {
                    records.Add(record);
                }
            }
        }

        return records;
    }
}
