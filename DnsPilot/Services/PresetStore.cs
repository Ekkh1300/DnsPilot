using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

using DnsPilot.Models;

namespace DnsPilot.Services;

/// <summary>
/// The list of resolvers the user picks from: the ones that ship with the app, plus anything
/// they add themselves.
/// </summary>
/// <remarks>
/// The built-in list is an embedded resource rather than a file beside the exe, because a
/// file beside the exe is one that an antivirus, a zip slip, or an over-eager cleanup can take
/// away and leave the app with an empty window and no explanation. Custom servers live in a
/// separate file under the user's own profile, so the two never touch each other - a reinstall
/// replaces one and keeps the other.
/// </remarks>
public sealed class PresetStore
{
    private const string CustomFileName = "custom-servers.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,

        // The file writes the family as "IPv4" rather than 0. System.Text.Json defaults enums to
        // their numbers, so without this converter the whole embedded list fails to deserialise
        // - every group throws, and the app opens with an empty list and no servers. The name is
        // also the right choice for a data file a person might read or hand-edit: 0 and 1 carry
        // no meaning and a reordered enum would silently swap every address's family.
        Converters = { new JsonStringEnumConverter() },
    };

    public string CustomPath { get; }

    public PresetStore(string? customDirectory = null)
    {
        CustomPath = Path.Combine(customDirectory ?? DefaultDirectory(), CustomFileName);
    }

    private static string DefaultDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DnsPilot");

    /// <summary>Loads the six built-in groups. Throws with a named cause if the data is unusable.</summary>
    public static IReadOnlyList<PresetGroup> LoadBuiltIn()
    {
        // typeof rather than Assembly.GetExecutingAssembly: the resource lives in the app's own
        // assembly, and this is also called from the test project, where "executing" would be
        // judged from a call made through inlining or a shim and the lookup would miss.
        var assembly = typeof(PresetStore).Assembly;
        const string resource = "DnsPilot.Data.presets.json";

        using var stream = assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            var names = string.Join(", ", assembly.GetManifestResourceNames());
            throw new InvalidOperationException(
                $"the built-in resolver list is missing from the app (expected {resource}; found: {names}).");
        }

        using var reader = new StreamReader(stream);
        var file = JsonSerializer.Deserialize<PresetFile>(reader.ReadToEnd(), JsonOptions);
        if (file?.Groups is null || file.Groups.Count == 0)
            throw new InvalidOperationException("the built-in resolver list is present but contains no groups.");

        return Normalize(file.Groups, "built-in");
    }

    /// <summary>
    /// Loads the user's own servers. Absence is not an error - a fresh install has no file - and
    /// neither is a file that fails to parse: the app still has to open with the built-in list
    /// in place, so a corrupt custom file is reported by the caller rather than thrown here.
    /// </summary>
    /// <remarks>
    /// The problem comes back as a <see cref="CustomProblem"/> - a Lang key and its arguments,
    /// not English words - because Services never knows which language the window is running in.
    /// Formatting happens at the UI edge, where the language is current; this is the same rule
    /// <c>LatencyResult</c> already follows for its error keys. The one argument that cannot be
    /// localised is the exception's own message: Windows phrases it in the system language, and
    /// repeating it verbatim is the honest way to carry the OS's reason through.
    /// </remarks>
    public (IReadOnlyList<DnsServer> Servers, CustomProblem? Problem) TryLoadCustom()
    {
        if (!File.Exists(CustomPath)) return ([], null);

        try
        {
            var file = JsonSerializer.Deserialize<PresetFile>(File.ReadAllText(CustomPath), JsonOptions);

            // Converted rather than returned as-is: the file's objects are a persistence shape,
            // and the list the window shows has to be DnsServer with its addresses already
            // checked. Counting what was dropped turns a silent difference in length into a
            // sentence the user can read.
            var usable = new List<DnsServer>();
            var skipped = 0;

            foreach (var group in file?.Groups ?? [])
            {
                var family = group.Family ?? DnsFamily.IPv4;

                foreach (var entry in group.Servers ?? [])
                {
                    var server = entry?.ToServer(family);
                    if (server is null || string.IsNullOrWhiteSpace(server.Primary) || !server.IsValid)
                    {
                        skipped++;
                        continue;
                    }
                    usable.Add(server);
                }
            }

            var problem = skipped > 0
                ? new CustomProblem("custom_skipped", [skipped])
                : null;

            return (usable, problem);
        }
        catch (JsonException ex)
        {
            return ([], new CustomProblem("custom_unreadable", [ex.Message]));
        }
        catch (IOException ex)
        {
            return ([], new CustomProblem("custom_unreadable", [ex.Message]));
        }
    }

    public void SaveCustom(IReadOnlyList<DnsServer> servers)
    {
        var dir = Path.GetDirectoryName(CustomPath)!;
        Directory.CreateDirectory(dir);

        var file = new PresetFile
        {
            Groups = [new PresetGroupFile
            {
                Id = "custom",
                Family = DnsFamily.IPv4,
                Group = "custom",
                Servers = servers.Select(s => new DnsServerFile
                {
                    Name = s.Name,
                    Country = s.Country,
                    Primary = s.Primary,
                    Secondary = s.Secondary,
                    Family = s.Family,
                }).ToList(),
            }],
        };

        // Written to a temp file and moved, so a crash mid-write cannot leave a half-written JSON
        // in place of a file that used to work.
        var temp = CustomPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file, JsonOptions));
        File.Move(temp, CustomPath, overwrite: true);
    }

    /// <summary>
    /// Validates after deserialisation rather than trusting the file. Drops what does not parse,
    /// because one bad address in a preset otherwise shows up as a netsh failure at the moment
    /// the user clicks Apply, with a message that names neither the server nor the problem.
    /// </summary>
    private static IReadOnlyList<PresetGroup> Normalize(IReadOnlyList<PresetGroupFile> groups, string source)
    {
        var normalized = new List<PresetGroup>();

        foreach (var g in groups)
        {
            var family = g.Family ?? DnsFamily.IPv4;
            var servers = (g.Servers ?? [])
                .Where(s => s is not null && !string.IsNullOrWhiteSpace(s.Primary))
                .Select(s => s.ToServer(family))
                .Where(s => s.IsValid)
                .ToList();

            if (servers.Count == 0) continue;
            normalized.Add(new PresetGroup(g.Id ?? "unknown", family, g.Group ?? "default", servers));
        }

        if (normalized.Count == 0)
            throw new InvalidOperationException($"the resolver list from {source} contained no usable servers.");

        return normalized;
    }
}

/// <summary>Why a saved custom list could not be used in full, as a Lang key and its arguments.
/// Services never formats them; the UI edge does, where the current language is known.</summary>
public sealed record CustomProblem(string Key, object[] Args);

// --- serialisation shapes. These deliberately do not reuse DnsServer: the file format is a
// persistence contract and letting a domain record double as one makes a rename in the model a
// silent break in every saved file. ---

internal sealed class PresetFile
{
    public string? Source { get; set; }
    public List<PresetGroupFile>? Groups { get; set; }
}

internal sealed class PresetGroupFile
{
    public string? Id { get; set; }
    public DnsFamily? Family { get; set; }
    public string? Group { get; set; }
    public List<DnsServerFile>? Servers { get; set; }
}

internal sealed class DnsServerFile
{
    public string? Name { get; set; }
    public string? Country { get; set; }
    public string? Primary { get; set; }
    public string? Secondary { get; set; }
    public DnsFamily? Family { get; set; }

    public DnsServer ToServer() => ToServer(DnsFamily.IPv4);

    /// <summary>
    /// Materialises the entry, taking the family from the group it sits in when the entry does
    /// not declare its own.
    /// </summary>
    /// <remarks>
    /// The built-in file states the family once per group rather than on all sixty-three
    /// entries, so a loader that defaulted each entry to IPv4 would take one look at every IPv6
    /// address, decide it did not match its family, and drop it - leaving the window with three
    /// groups instead of six and no IPv6 resolver available at all.
    /// </remarks>
    public DnsServer ToServer(DnsFamily fallback) => new(
        Name: Name ?? Primary ?? "",
        Country: Country ?? "",
        Primary: Primary ?? "",
        Secondary: Secondary ?? "",
        Family: Family ?? fallback);
}
