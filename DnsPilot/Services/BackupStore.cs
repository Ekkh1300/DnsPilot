using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

using DnsPilot.Models;

namespace DnsPilot.Services;

/// <summary>What one adapter was set to before DnsPilot changed it.</summary>
public sealed record BackupEntry(
    string InterfaceId,
    string AdapterName,
    DnsFamily Family,
    bool WasDhcp,
    IReadOnlyList<string> Servers,
    string SavedAt);

/// <summary>
/// Remembers the resolver an adapter had before the first switch, so "put it back" means back to
/// what it was rather than back to a guess.
/// </summary>
/// <remarks>
/// The entry is written the moment a change is about to be made, not afterwards: a change that
/// fails half-way is exactly when a backup is most needed, and writing afterwards would record
/// nothing for the run that broke.
///
/// Only the first state is kept per adapter. After a day of trying different resolvers, the
/// thing the user wants back is the one the machine had when they started, not the one they had
/// three switches ago - and a growing history would be a file that has to be managed.
/// </remarks>
public sealed class BackupStore
{
    private const string FileName = "original-dns.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,

        // Same reason as in PresetStore: the family is stored as a name so the file stays
        // readable and a change to the enum's order cannot silently reinterpret it.
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _gate = new();

    public BackupStore(string? directory = null)
    {
        var dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DnsPilot");
        _path = Path.Combine(dir, FileName);
    }

    public string Path_ => _path;

    private Dictionary<string, BackupEntry> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new Dictionary<string, BackupEntry>();
            return JsonSerializer.Deserialize<Dictionary<string, BackupEntry>>(File.ReadAllText(_path), JsonOptions)
                   ?? new Dictionary<string, BackupEntry>();
        }
        catch (JsonException)
        {
            // A corrupt history must not stop the app from working. Losing "the original" is
            // worse than nothing, so the file is left in place for the user to look at rather
            // than overwritten with an empty one on the next save.
            return new Dictionary<string, BackupEntry>();
        }
        catch (IOException)
        {
            return new Dictionary<string, BackupEntry>();
        }
    }

    private void Save(Dictionary<string, BackupEntry> entries)
    {
        var dir = System.IO.Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entries, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }

    private static string Key(string interfaceId, DnsFamily family) => $"{interfaceId}|{family}";

    public bool Has(string interfaceId, DnsFamily family)
    {
        lock (_gate) return Load().ContainsKey(Key(interfaceId, family));
    }

    /// <summary>
    /// Records the current state unless something is already recorded. Returns whether this call
    /// is the one that stored it, so the caller can tell an original from a later switch.
    /// </summary>
    public bool RememberIfAbsent(AdapterInfo adapter, DnsFamily family)
    {
        var state = adapter.StateFor(family);
        if (state is null) return false;

        lock (_gate)
        {
            var entries = Load();
            var key = Key(adapter.Id, family);
            if (entries.ContainsKey(key)) return false;

            entries[key] = new BackupEntry(
                InterfaceId: adapter.Id,
                AdapterName: adapter.Alias,
                Family: family,
                WasDhcp: state.IsDhcp,
                Servers: state.StaticServers,
                SavedAt: DateTime.Now.ToString("o"));

            Save(entries);
            return true;
        }
    }

    public BackupEntry? Get(string interfaceId, DnsFamily family)
    {
        lock (_gate) return Load().GetValueOrDefault(Key(interfaceId, family));
    }

    public void Forget(string interfaceId, DnsFamily family)
    {
        lock (_gate)
        {
            var entries = Load();
            if (entries.Remove(Key(interfaceId, family))) Save(entries);
        }
    }
}
