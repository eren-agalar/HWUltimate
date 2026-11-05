using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NovaHardware.Core.Abstractions;

namespace NovaHardware.Infrastructure.Preferences;

/// <summary>Stores a preference document as JSON next to the other per-user data. Writes are atomic.</summary>
public sealed class JsonPreferenceStore<T> : IPreferenceStore<T> where T : class, new()
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();

    public JsonPreferenceStore(string path, ILogger logger)
    {
        _path = path;
        _logger = logger;
    }

    public T Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path))
                {
                    return JsonSerializer.Deserialize<T>(File.ReadAllText(_path), Options) ?? new T();
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Preference file {Path} unreadable; defaults used", _path);
            }

            return new T();
        }
    }

    public void Save(T value)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not save preferences to {Path}", _path);
            }
        }
    }
}
