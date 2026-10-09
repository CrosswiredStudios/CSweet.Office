using System.Text.Json;
using CSweet.Office.Runtime.Abstractions;

namespace CSweet.Office.Runtime.HyperV.Helper;

// RuntimeHost supplies the opaque UUID only after validating signed creation authority.
// The existing create/destroy operations share this durable, provider-owned identity.
internal sealed record HyperVAttempt(Guid Key, Guid WorkloadId, WorkloadKind Kind,
    Guid? InstanceId = null, bool Stopped = false)
{
    public string VmName => $"CSweet-{Kind}-{Key:N}";
}

internal sealed class HyperVAttemptJournal : IDisposable
{
    private readonly string _path;
    private readonly FileStream _lock;
    public HyperVAttemptJournal(HyperVHelperPaths paths, Guid key)
    {
        if (key == Guid.Empty) throw new InvalidDataException("Invalid attempt key.");
        var root = Path.Combine(paths.DataRoot, "attempts");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, $"{key:N}.json");
        _lock = new FileStream(Path.Combine(root, $"{key:N}.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }
    public HyperVAttempt? Read() => File.Exists(_path)
        ? JsonSerializer.Deserialize<HyperVAttempt>(File.ReadAllBytes(_path)) ?? throw new InvalidDataException("Invalid creation journal.")
        : null;
    public void Save(HyperVAttempt entry)
    {
        var temporary = _path + ".new";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                   4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(file, entry);
            file.Flush(true);
        }
        File.Move(temporary, _path, true);
    }
    public void Dispose() => _lock.Dispose();
}
