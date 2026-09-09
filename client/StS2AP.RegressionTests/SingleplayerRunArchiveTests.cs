using StS2AP.Persistence;
using Xunit;

namespace StS2AP.RegressionTests;

public sealed class SingleplayerRunArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ap-run-archive-" + Guid.NewGuid().ToString("N"));
    private readonly SingleplayerRunArchive.Identity _owner = new("seed", 0, 1);
    private SingleplayerRunArchive Archive => new(_root);
    private SingleplayerRunArchive.Run NewRun() => new() { Id = Guid.NewGuid(), Owner = _owner, Character = "IRONCLAD" };

    [Fact]
    public void SixMilestonesPersistAndReplayingEarlierOnePreservesLaterOnes()
    {
        var run = NewRun();
        foreach (string key in SingleplayerRunArchive.Milestones) Archive.Save(run, key, key, 1);
        Archive.Save(run, "1-boss", "replayed boss", 17);
        var fresh = new SingleplayerRunArchive(_root);
        Assert.Equal(6, fresh.Read(run.Id).Checkpoints.Count);
        Assert.Equal("replayed boss", fresh.Load(run.Id, "1-boss", _owner, run.Character));
        Assert.Equal("3-treasure", fresh.Load(run.Id, "3-treasure", _owner, run.Character));
        Assert.Equal(6, Directory.GetFiles(Path.Combine(_root, run.Id.ToString("N")), "run-*.save").Length);
    }

    [Fact]
    public void SameCharacterRunsRemainSeparateAndNativeSaveIsUntouched()
    {
        Directory.CreateDirectory(_root);
        string native = Path.Combine(_root, "current_run.save");
        File.WriteAllText(native, "unrelated modded save");
        var first = NewRun(); var second = NewRun();
        Archive.Save(first, "1-boss", "first", 17);
        Archive.Save(second, "1-boss", "second", 17);
        Assert.Equal(2, Archive.List().Count());
        Assert.Equal("first", Archive.Load(first.Id, "1-boss", _owner, first.Character));
        Assert.Equal("unrelated modded save", File.ReadAllText(native));
    }

    [Fact]
    public void WrongSeedTeamSlotAndCharacterCannotLoad()
    {
        var run = NewRun(); Archive.Save(run, "1-boss", "payload", 17);
        foreach (var identity in new[] { _owner with { Seed = "other" }, _owner with { Team = 1 }, _owner with { Slot = 2 } })
            Assert.Throws<InvalidDataException>(() => Archive.Load(run.Id, "1-boss", identity, run.Character));
        Assert.Throws<InvalidDataException>(() => Archive.Load(run.Id, "1-boss", _owner, "SILENT"));
    }

    [Fact]
    public void CorruptCheckpointDoesNotDamageOtherMilestones()
    {
        var run = NewRun();
        Archive.Save(run, "1-boss", "boss", 17); Archive.Save(run, "1-treasure", "treasure", 8);
        var snapshot = Archive.Read(run.Id).Checkpoints["1-boss"];
        File.WriteAllText(Path.Combine(_root, run.Id.ToString("N"), snapshot.FileName), "broken");
        Assert.Throws<InvalidDataException>(() => Archive.Load(run.Id, "1-boss", _owner, run.Character));
        Assert.Equal("treasure", Archive.Load(run.Id, "1-treasure", _owner, run.Character));
    }

    [Fact]
    public void CompletedRunRemainsLoadableAndDoesNotDeleteSnapshots()
    {
        var run = NewRun(); Archive.Save(run, "3-treasure", "last", 40);
        Archive.MarkEnded(run.Id, true);
        Assert.Equal("Completed", Archive.Read(run.Id).Status);
        Assert.Equal("last", Archive.Load(run.Id, "3-treasure", _owner, run.Character));
    }

    [Theory]
    [InlineData("1-ancient", true, 0, true)]
    [InlineData("1-boss", true, 1, true)]
    [InlineData("2-treasure", true, 1, false)]
    [InlineData("2-boss", true, 1, false)]
    [InlineData("2-treasure", true, 2, true)]
    [InlineData("3-treasure", true, 2, false)]
    [InlineData("3-treasure", true, 3, true)]
    [InlineData("3-treasure", false, 0, true)]
    [InlineData("2-ancient", false, 3, false)]
    [InlineData("3-ancient", false, 3, false)]
    [InlineData("3-boss", false, 3, false)]
    public void MilestonesPreserveBossPriorityAndAncientGates(string key, bool start, int unlocked, bool expected) =>
        Assert.Equal(expected, SingleplayerRunArchive.IsAllowed(key, start, unlocked));

    [Fact]
    public void UnreadableManifestIsListedWithoutHidingHealthyRuns()
    {
        var good = NewRun(); var bad = NewRun();
        Archive.Save(good, "1-boss", "good", 17); Archive.Save(bad, "1-boss", "bad", 17);
        File.WriteAllText(Path.Combine(_root, bad.Id.ToString("N"), "metadata.json"), "{");
        Assert.Single(Archive.List(), entry => entry.Run != null);
        Assert.Single(Archive.List(), entry => entry.Error != null);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
