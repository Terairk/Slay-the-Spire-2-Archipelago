using System.Text;
using System.Text.Json;

namespace StS2AP.Persistence;

/// <summary>One manifest per run, with six bounded milestones and immutable shared save payloads.</summary>
internal sealed class SingleplayerRunArchive(string root)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    internal sealed record Identity(string Seed, int Team, int Slot);
    internal sealed record Snapshot(string Key, string FileName, string Hash, DateTimeOffset SavedAt, int Floor);
    internal sealed class Run
    {
        public int Version { get; set; } = 1;
        public Guid Id { get; set; }
        public Identity Owner { get; set; } = new("", -1, -1);
        public string Character { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public string Status { get; set; } = "Active";
        public bool StartOfAct { get; set; }
        public Dictionary<string, Snapshot> Checkpoints { get; set; } = new();
    }

    internal static readonly string[] Milestones =
        ["1-ancient", "1-treasure", "1-boss", "2-treasure", "2-boss", "3-treasure"];

    internal static bool IsAllowed(string key, bool startOfAct, int unlockedAct) =>
        Milestones.Contains(key) && (!startOfAct || key[0] - '0' <= Math.Max(1, unlockedAct));

    private string DirectoryFor(Guid id) => Path.Combine(root, id.ToString("N"));
    private string Manifest(Guid id) => Path.Combine(DirectoryFor(id), "metadata.json");

    public IEnumerable<(Run? Run, string? Error)> List()
    {
        if (!Directory.Exists(root)) yield break;
        foreach (string directory in Directory.EnumerateDirectories(root).Order())
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out Guid id)) continue;
            Run? run = null;
            string? error = null;
            try { run = Read(id); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            { error = $"{id:N}: {ex.Message}"; }
            yield return (run, error);
        }
    }

    public Run Read(Guid id)
    {
        Run run = JsonSerializer.Deserialize<Run>(File.ReadAllText(Manifest(id)), Options)
            ?? throw new InvalidDataException("Empty AP run metadata.");
        if (run.Version != 1 || run.Id != id || run.Owner == null
            || string.IsNullOrWhiteSpace(run.Owner.Seed) || run.Owner.Team < 0 || run.Owner.Slot < 0
            || string.IsNullOrWhiteSpace(run.Character) || run.Checkpoints == null
            || run.Checkpoints.Any(pair => pair.Value == null || pair.Key != pair.Value.Key
                || !Milestones.Contains(pair.Key)))
            throw new InvalidDataException("Invalid or unsupported AP run metadata.");
        return run;
    }

    public void Save(Run run, string key, string payload, int floor)
    {
        if (!Milestones.Contains(key)) throw new ArgumentException("Unknown AP checkpoint.", nameof(key));
        // Re-read before publishing: loading an earlier milestone must preserve later milestones.
        Run next = File.Exists(Manifest(run.Id)) ? Read(run.Id) : run;
        if (next.Owner != run.Owner || next.Character != run.Character)
            throw new InvalidDataException("AP run identity changed.");
        var stored = CampaignSaveFiles.StoreBytes(DirectoryFor(run.Id), Encoding.UTF8.GetBytes(payload));
        next.Checkpoints.TryGetValue(key, out Snapshot? previous);
        next.Checkpoints[key] = new(key, stored.FileName, stored.Hash, DateTimeOffset.UtcNow, floor);
        next.Status = "Active";
        Write(next);
        // Only prune after metadata publication. A failed write never removes an older payload.
        if (previous != null && next.Checkpoints.Values.All(s => s.FileName != previous.FileName))
        {
            try { File.Delete(CampaignSaveFiles.GetPath(DirectoryFor(run.Id), previous.FileName, previous.Hash)); }
            catch (IOException) { /* An orphan is preferable to failing a published save. */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    public string Load(Guid id, string key, Identity owner, string character)
    {
        Run run = Read(id);
        if (run.Owner != owner || run.Character != character)
            throw new InvalidDataException("This checkpoint belongs to another AP slot or character.");
        if (!run.Checkpoints.TryGetValue(key, out Snapshot? snapshot))
            throw new InvalidDataException("The checkpoint is missing.");
        byte[] bytes = File.ReadAllBytes(CampaignSaveFiles.GetPath(DirectoryFor(id), snapshot.FileName, snapshot.Hash));
        if (!string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
            snapshot.Hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The checkpoint checksum does not match.");
        return Encoding.UTF8.GetString(bytes);
    }

    public void MarkEnded(Guid id, bool victory)
    {
        if (!File.Exists(Manifest(id))) return;
        Run run = Read(id);
        run.Status = victory ? "Completed" : "Ended";
        Write(run);
    }

    private void Write(Run run)
    {
        Directory.CreateDirectory(DirectoryFor(run.Id));
        string destination = Manifest(run.Id);
        string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(run, Options));
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
