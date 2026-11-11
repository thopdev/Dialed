using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Dialed.Api.Push;

/// <summary>
/// An in-memory push store that also keeps its data in one JSON file, so a restart does not forget devices, switches or which stock alerts
/// were already sent (#259). For development and a single instance only: the file holds push endpoints and keys, so it is created readable
/// by its owner alone, and it never leaves the machine. In Azure the Cosmos DB store (#260) replaces it.
/// </summary>
public sealed class FilePushStore : InMemoryPushStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string path;
    private readonly ILogger<FilePushStore> log;
    private readonly Lock fileGate = new();

    public FilePushStore(string path, ILogger<FilePushStore> log)
    {
        this.path = Path.GetFullPath(path);
        this.log = log;
        Load();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<PushStoreSnapshot>(File.ReadAllText(path), Json) is { } snapshot)
            {
                Restore(snapshot);
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A file that cannot be read starts an empty store (and is overwritten at the next change); it must never stop the API.
            log.LogWarning(e, "The push store file could not be read; starting empty.");
        }
    }

    protected override void OnChanged()
    {
        lock (fileGate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(Snapshot(), Json));
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                File.Move(temp, path, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Losing the file's freshness is better than failing the request that changed something.
                log.LogWarning(e, "The push store file could not be written.");
            }
        }
    }
}
