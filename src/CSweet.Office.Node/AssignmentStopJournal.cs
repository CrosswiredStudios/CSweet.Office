using System.Text.Json;
using CSweet.Office.Runtime.Abstractions;

namespace CSweet.Office.Node;

internal sealed record AssignmentStopEntry(Guid OfficeId, Guid AssignmentId, long FencingEpoch,
    string ProviderId, string Phase, IsolationWorkloadHandle? Handle = null);

/// <summary>Creation intent, cleanup outbox and permanent replay tombstones for exact attempts.</summary>
internal sealed class AssignmentStopJournal(OfficeOptions options)
{
    private readonly string _root = Path.Combine(options.ResolveStateDirectory(), "assignment-stops");
    private readonly object _gate = new();
    private string? _cursor;

    public bool TryBegin(Guid officeId, Guid assignmentId, long epoch, string providerId)
    {
        lock (_gate)
        {
            var entry = new AssignmentStopEntry(officeId, assignmentId, epoch, providerId, "prepared");
            if (File.Exists(PathFor(entry)) || File.Exists(PathFor(entry) + ".ack")) return false;
            Save(entry);
            return true;
        }
    }

    public AssignmentStopEntry Read(Guid officeId, Guid assignmentId, long epoch)
    {
        lock (_gate) return ReadFile(PathFor(new(officeId, assignmentId, epoch, "", "")));
    }

    public void BeginCreate(AssignmentStopEntry entry) => Transition(entry, "prepared", "creating");

    public void Created(AssignmentStopEntry entry, IsolationWorkloadHandle handle)
    {
        if (handle.ProviderId != entry.ProviderId || string.IsNullOrWhiteSpace(handle.ProviderInstanceId) ||
            handle.ProviderInstanceId.Length > 256)
            throw new InvalidDataException("The provider returned an invalid cleanup handle.");
        Transition(entry, "creating", "created", handle);
    }

    private void Transition(AssignmentStopEntry entry, string expected, string phase, IsolationWorkloadHandle? handle = null)
    {
        lock (_gate)
        {
            var current = ReadFile(PathFor(entry));
            if (current.Phase != expected) throw new InvalidDataException("Invalid assignment cleanup transition.");
            Save(current with { Phase = phase, Handle = handle ?? current.Handle });
        }
    }

    public async Task<bool> CleanupAsync(AssignmentStopEntry entry, IAgentIsolationProvider? provider,
        CancellationToken cancellationToken)
    {
        if (entry.Phase == "stopped") return true;
        if (entry.Phase == "creating") return false; // Creation outcome is unknown; never manufacture proof.
        if (entry.Phase == "created")
        {
            if (entry.Handle is null || provider is null || provider.Descriptor.ProviderId != entry.ProviderId) return false;
            await provider.DestroyAsync(entry.Handle, cancellationToken);
            var status = await provider.InspectAsync(entry.Handle, cancellationToken);
            if (status is not null && (status.Handle != entry.Handle || status.State != IsolationWorkloadState.Destroyed)) return false;
        }
        else if (entry.Phase != "prepared") throw new InvalidDataException("Unknown cleanup phase.");
        Transition(entry, entry.Phase, "stopped");
        return true;
    }

    public void Acknowledge(Guid officeId, Guid assignmentId, long epoch)
    {
        lock (_gate)
        {
            var path = PathFor(new(officeId, assignmentId, epoch, "", ""));
            if (!File.Exists(path)) return;
            if (ReadFile(path).Phase != "stopped") return;
            // Keep a tombstone: an old signed assignment must not be created after its receipt.
            File.Move(path, path + ".ack", true);
        }
    }

    public IReadOnlyList<AssignmentStopEntry> Discover(Guid officeId, int maximum = 8)
    {
        lock (_gate)
        {
            var directory = Path.Combine(_root, officeId.ToString("N"));
            if (!Directory.Exists(directory)) return [];
            // Rotate through retained/rejected reports so they cannot starve later cleanup.
            var files = Directory.EnumerateFiles(directory, "*.json")
                .Where(x => _cursor is null || string.CompareOrdinal(x, _cursor) > 0)
                .Order(StringComparer.Ordinal).Take(maximum).ToArray();
            if (files.Length == 0) { _cursor = null; return []; }
            _cursor = files[^1];
            return files.Select(ReadFile).ToArray();
        }
    }

    public bool HasUnconfirmed(Guid officeId, Guid assignmentId)
    {
        lock (_gate)
        {
            var directory = Path.Combine(_root, officeId.ToString("N"));
            return Directory.Exists(directory) && Directory.EnumerateFiles(directory, $"{assignmentId:N}-*.json")
                .Any(path => ReadFile(path).Phase != "stopped");
        }
    }

    public void RestoreMaintenance(Guid officeId, OfficeStateStore stateStore)
    {
        lock (_gate)
        {
            var directory = Path.Combine(_root, officeId.ToString("N"));
            if (!Directory.Exists(directory)) return;
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                var entry = ReadFile(path);
                if (entry.Phase != "stopped") stateStore.MarkAssignmentActive(entry.AssignmentId);
            }
        }
    }

    private AssignmentStopEntry ReadFile(string path)
    {
        var entry = JsonSerializer.Deserialize<AssignmentStopEntry>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Empty cleanup journal entry.");
        if (PathFor(entry) != path) throw new InvalidDataException("Cleanup journal identity mismatch.");
        return entry;
    }

    private string PathFor(AssignmentStopEntry entry) => Path.Combine(_root, entry.OfficeId.ToString("N"),
        $"{entry.AssignmentId:N}-{entry.FencingEpoch:D20}.json");

    private void Save(AssignmentStopEntry entry)
    {
        var path = PathFor(entry);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".new";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                   4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, entry);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, true);
    }
}
