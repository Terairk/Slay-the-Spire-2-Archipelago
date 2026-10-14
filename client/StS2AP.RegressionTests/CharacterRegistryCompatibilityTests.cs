using System.Runtime.Loader;
using System.Text.Json;
using StS2AP.Data;
using Xunit;

namespace StS2AP.RegressionTests;

public sealed class CharacterRegistryCompatibilityTests
{
    [ArtifactFact("STS2AP_TEST_ASSEMBLY")]
    [Trait("Category", "Manifest")]
    public void OldAndNamedCharacterConfigurationsKeepTheirNamesAndIndependentIdBlocks()
    {
        string path = Path.GetFullPath(Environment.GetEnvironmentVariable("STS2AP_TEST_ASSEMBLY")!);
        var context = new AssemblyLoadContext("character-registry", isCollectible: true);
        context.Resolving += (_, name) =>
        {
            string dependency = Path.Combine(Path.GetDirectoryName(path)!, name.Name + ".dll");
            return File.Exists(dependency) ? context.LoadFromAssemblyPath(dependency) : null;
        };
        try
        {
            Type type = context.LoadFromAssemblyPath(path).GetType("StS2AP.Models.CharacterConfig", true)!;
            var configurations = new[]
            {
                (Name: "Custom Character 1", InternalId: "WATCHER-WATCHER", Block: 6, ModNum: 1),
                (Name: "Watcher", InternalId: "WATCHER-WATCHER", Block: 51, ModNum: 1),
                (Name: "Custom Character 1", InternalId: "UNKNOWN-MOD", Block: 6, ModNum: 2),
                (Name: "Future Vanilla", InternalId: "FUTURE_VANILLA", Block: 21, ModNum: 0),
            };
            foreach (var entry in configurations)
            {
                string json = JsonSerializer.Serialize(new
                {
                    Name = entry.Name, OptionName = entry.InternalId, OfficialName = entry.InternalId,
                    CharOffset = entry.Block, ModNum = entry.ModNum, Seed = "seed", Locked = false,
                    Ascension = Array.Empty<string>(),
                });
                object config = JsonSerializer.Deserialize(json, type)!;
                // The same configuration crosses the multiplayer JSON snapshot boundary.
                config = JsonSerializer.Deserialize(JsonSerializer.Serialize(config, type), type)!;
                Assert.Equal(entry.Name, type.GetProperty("Name")!.GetValue(config));
                Assert.Equal(entry.InternalId, type.GetProperty("OfficialName")!.GetValue(config));
                Assert.Equal(entry.Block, type.GetProperty("CharOffset")!.GetValue(config));
                Assert.Equal(entry.ModNum, type.GetProperty("ModNum")!.GetValue(config));
                long itemId = entry.Block * ArchipelagoIdCodec.BlockSize + 4;
                Assert.Equal(entry.Block, ArchipelagoIdCodec.GetAPCharacterNumberFromItemId(itemId));
                Assert.Equal(4, ArchipelagoIdCodec.GetCharacterItemTypeId(itemId));
                Assert.True(ArchipelagoIdCodec.TryComposeLocationId(79, entry.Block, out long locationId));
                Assert.Equal((entry.Block - 1) * ArchipelagoIdCodec.BlockSize + 79, locationId);
            }
        }
        finally
        {
            context.Unload();
        }
    }
}
