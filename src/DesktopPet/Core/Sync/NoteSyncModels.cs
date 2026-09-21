using System.Security.Cryptography;
using System.Text;

namespace DesktopPet.Core.Sync;

public sealed record NoteVersion(Guid RevisionId, Guid DeviceId, long Counter);

public sealed record NoteSyncRecord(
    Guid NoteId,
    string Content,
    bool IsConflict,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? DeletedAtUtc,
    NoteVersion Version,
    Dictionary<string, long> VersionVector);

public sealed class NoteReplicaFile
{
    public const string ExpectedFormat = "desktop-pet.note-replica";
    public const int CurrentSchemaVersion = 1;

    public string Format { get; init; } = ExpectedFormat;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public Guid ReplicaId { get; init; }
    public IReadOnlyList<NoteSyncRecord> Notes { get; init; } = [];
}

public static class NoteMergeEngine
{
    public const int MaximumContentLength = 10_000;

    public static IReadOnlyList<NoteSyncRecord> Merge(
        IEnumerable<NoteSyncRecord> existing,
        IEnumerable<NoteSyncRecord> incoming)
    {
        var records = new Dictionary<Guid, NoteSyncRecord>();
        var candidates = existing.Concat(incoming)
            .Where(IsValid)
            .OrderBy(note => note.NoteId)
            .ThenBy(note => note.Version.Counter)
            .ThenBy(note => note.Version.DeviceId)
            .ThenBy(note => note.Version.RevisionId);
        foreach (var candidate in candidates) MergeInto(records, candidate);

        return records.Values.OrderBy(note => note.NoteId).ToArray();
    }

    public static bool IsValid(NoteSyncRecord note)
    {
        if (note.Content is null || note.Version is null || note.VersionVector is null ||
            note.NoteId == Guid.Empty || note.Version.RevisionId == Guid.Empty ||
            note.Version.DeviceId == Guid.Empty || note.Version.Counter <= 0 ||
            note.Content.Length > MaximumContentLength || note.VersionVector.Count == 0)
            return false;

        return note.VersionVector.TryGetValue(note.Version.DeviceId.ToString("D"), out var counter) &&
               counter >= note.Version.Counter;
    }

    public static Guid ConflictNoteId(Guid noteId, Guid revisionId) =>
        DeterministicGuid($"desktop-pet-note-conflict:{noteId:D}:{revisionId:D}");

    private static IReadOnlyList<NoteSyncRecord> MergePair(NoteSyncRecord left, NoteSyncRecord right)
    {
        if (left.Version.RevisionId == right.Version.RevisionId)
            return [WithVector(left, MergeVectors(left.VersionVector, right.VersionVector))];

        var leftDominates = Dominates(left.VersionVector, right.VersionVector);
        var rightDominates = Dominates(right.VersionVector, left.VersionVector);
        var mergedVector = MergeVectors(left.VersionVector, right.VersionVector);
        if (left.DeletedAtUtc is not null || right.DeletedAtUtc is not null)
        {
            if (left.DeletedAtUtc is not null && right.DeletedAtUtc is not null)
                return [WithVector(MaxVersion(left, right), mergedVector)];

            var deletion = left.DeletedAtUtc is not null ? left : right;
            var edited = ReferenceEquals(deletion, left) ? right : left;
            var deletionDominates = ReferenceEquals(deletion, left) ? leftDominates : rightDominates;
            if (deletionDominates) return [WithVector(deletion, mergedVector)];
            var results = new List<NoteSyncRecord> { WithVector(deletion, mergedVector) };
            results.Add(CreateConflictCopy(edited, mergedVector));
            return results;
        }

        if (leftDominates && !rightDominates) return [Clone(left)];
        if (rightDominates && !leftDominates) return [Clone(right)];

        var winner = MaxVersion(left, right);
        var loser = ReferenceEquals(winner, left) ? right : left;
        return [WithVector(winner, mergedVector), CreateConflictCopy(loser, mergedVector)];
    }

    private static void MergeInto(IDictionary<Guid, NoteSyncRecord> records, NoteSyncRecord candidate)
    {
        if (!records.TryGetValue(candidate.NoteId, out var current))
        {
            records[candidate.NoteId] = Clone(candidate);
            return;
        }

        var results = MergePair(current, candidate);
        records[candidate.NoteId] = results[0];
        for (var index = 1; index < results.Count; index++) MergeInto(records, results[index]);
    }

    private static NoteSyncRecord CreateConflictCopy(NoteSyncRecord source, Dictionary<string, long> mergedVector)
    {
        var noteId = ConflictNoteId(source.NoteId, source.Version.RevisionId);
        var revisionId = DeterministicGuid($"desktop-pet-note-conflict-revision:{source.NoteId:D}:{source.Version.RevisionId:D}");
        return new NoteSyncRecord(
            noteId,
            source.Content,
            true,
            source.CreatedAtUtc,
            source.UpdatedAtUtc,
            null,
            new NoteVersion(revisionId, source.Version.DeviceId, source.Version.Counter),
            new Dictionary<string, long>(mergedVector, StringComparer.Ordinal));
    }

    private static NoteSyncRecord MaxVersion(NoteSyncRecord left, NoteSyncRecord right)
    {
        var counter = left.Version.Counter.CompareTo(right.Version.Counter);
        if (counter != 0) return counter > 0 ? left : right;
        var device = string.CompareOrdinal(left.Version.DeviceId.ToString("D"), right.Version.DeviceId.ToString("D"));
        if (device != 0) return device > 0 ? left : right;
        return string.CompareOrdinal(left.Version.RevisionId.ToString("D"), right.Version.RevisionId.ToString("D")) >= 0 ? left : right;
    }

    private static bool Dominates(IReadOnlyDictionary<string, long> left, IReadOnlyDictionary<string, long> right)
    {
        var strictlyGreater = false;
        foreach (var (device, rightCounter) in right)
        {
            var leftCounter = left.TryGetValue(device, out var value) ? value : 0;
            if (leftCounter < rightCounter) return false;
            if (leftCounter > rightCounter) strictlyGreater = true;
        }
        if (!strictlyGreater)
            strictlyGreater = left.Any(pair => !right.TryGetValue(pair.Key, out var value) || pair.Value > value);
        return strictlyGreater;
    }

    private static Dictionary<string, long> MergeVectors(
        IReadOnlyDictionary<string, long> left,
        IReadOnlyDictionary<string, long> right)
    {
        var values = new Dictionary<string, long>(left, StringComparer.Ordinal);
        foreach (var (device, counter) in right)
        {
            if (!values.TryGetValue(device, out var current) || counter > current) values[device] = counter;
        }
        return values.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    private static NoteSyncRecord WithVector(NoteSyncRecord source, Dictionary<string, long> vector) =>
        source with { VersionVector = CanonicalVector(vector) };

    private static NoteSyncRecord Clone(NoteSyncRecord source) =>
        source with { VersionVector = CanonicalVector(source.VersionVector) };

    private static Dictionary<string, long> CanonicalVector(IEnumerable<KeyValuePair<string, long>> vector) =>
        vector.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static Guid DeterministicGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        Span<byte> guidBytes = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(guidBytes);
        return new Guid(guidBytes);
    }
}
