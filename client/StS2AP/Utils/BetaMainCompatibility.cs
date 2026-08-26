using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Managers;

namespace StS2AP.Utils;

/// <summary>
/// Bridges game API differences between the public 0.107.1 branch and newer beta branches.
/// Keep direct references to renamed or removed game types inside this reflection boundary.
/// </summary>
public static class BetaMainCompatibility
{
    /// <summary>
    /// Compares an action-synchronizer phase by its runtime name. Newer game versions inserted
    /// PreCombatSetup into this enum, shifting the numeric values of every later member. Direct
    /// enum comparisons compiled against the public branch therefore misidentify those phases.
    /// </summary>
    public static bool IsActionSynchronizerCombatState(
        ActionSynchronizerCombatState state,
        string expectedName) =>
        string.Equals(Enum.GetName(state), expectedName, StringComparison.Ordinal);

    /// <summary>
    /// Resolves a run-save path without binding the compiled mod to the public branch's
    /// two-parameter overload or the beta branch's three-parameter overload.
    /// </summary>
    public static string GetRunSavePath(int profileId, string fileName)
    {
        var betaMethod = AccessTools.Method(
            typeof(RunSaveManager),
            nameof(RunSaveManager.GetRunSavePath),
            new[] { typeof(int), typeof(string), typeof(bool?) }
        );
        if (betaMethod != null)
        {
            return betaMethod.Invoke(null, new object?[] { profileId, fileName, null }) as string
                ?? throw new InvalidCastException(
                    $"{typeof(RunSaveManager).FullName}.GetRunSavePath returned a non-string value."
                );
        }

        var mainMethod = AccessTools.Method(
            typeof(RunSaveManager),
            nameof(RunSaveManager.GetRunSavePath),
            new[] { typeof(int), typeof(string) }
        ) ?? throw new MissingMethodException(
            typeof(RunSaveManager).FullName,
            nameof(RunSaveManager.GetRunSavePath)
        );
        return mainMethod.Invoke(null, new object?[] { profileId, fileName }) as string
            ?? throw new InvalidCastException(
                $"{typeof(RunSaveManager).FullName}.GetRunSavePath returned a non-string value."
            );
    }

    /// <summary>
    /// Resolves the authoritative MegaCrit host from live network state. Do not duplicate this
    /// value in AP run data: a host process owns its own <see cref="INetGameService.NetId"/>,
    /// while a client is explicitly told the same identity by
    /// <see cref="NetClientGameService.HostNetId"/>. AP does not support host migration.
    /// </summary>
    public static bool TryGetHostNetId(INetGameService netService, out ulong hostNetId)
    {
        hostNetId = default;
        if (netService.Type == NetGameType.Singleplayer)
        {
            hostNetId = netService.NetId;
            return true;
        }

        if (!netService.IsConnected)
            return false;

        switch (netService.Type)
        {
            case NetGameType.Host:
                hostNetId = netService.NetId;
                return true;
            case NetGameType.Client when netService is NetClientGameService client:
                hostNetId = client.HostNetId;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Enumerates StartRunLobby player IDs without binding to the public-branch LobbyPlayer or
    /// beta-branch StartRunLobbyPlayer element type. Keep this reflection boundary until both
    /// supported game branches expose one stable lobby-player type.
    /// </summary>
    public static IReadOnlyList<ulong> GetLobbyPlayerNetIds(object lobby)
    {
        ArgumentNullException.ThrowIfNull(lobby);
        object players = AccessTools.Property(lobby.GetType(), "Players")?.GetValue(lobby)
            ?? throw new MissingMemberException(lobby.GetType().FullName, "Players");
        return ReadPlayerNetIds(players, $"{lobby.GetType().FullName}.Players");
    }

    /// <summary>
    /// Reads the complete new-run lobby lineup without binding to the public branch's
    /// LobbyPlayer or the beta branch's renamed player type.
    /// </summary>
    public static IReadOnlyList<(ulong NetId, string CharacterId)> GetLobbyPlayerCharacters(
        object lobby)
    {
        ArgumentNullException.ThrowIfNull(lobby);
        object players = AccessTools.Property(lobby.GetType(), "Players")?.GetValue(lobby)
            ?? throw new MissingMemberException(lobby.GetType().FullName, "Players");
        if (players is not System.Collections.IEnumerable sequence)
            throw new InvalidCastException($"{lobby.GetType().FullName}.Players is not enumerable.");

        var result = new List<(ulong NetId, string CharacterId)>();
        foreach (object player in sequence)
        {
            Type playerType = player.GetType();
            object? rawNetId = AccessTools.Field(playerType, "id")?.GetValue(player)
                ?? AccessTools.Property(playerType, "id")?.GetValue(player);
            object? rawCharacter = AccessTools.Field(playerType, "character")?.GetValue(player)
                ?? AccessTools.Property(playerType, "character")?.GetValue(player);
            if (rawNetId is not ulong netId || rawCharacter is not CharacterModel character)
            {
                throw new InvalidCastException(
                    $"Could not read the player identity and character from {playerType.FullName}."
                );
            }
            result.Add((netId, character.Id.Entry));
        }
        return result;
    }

    /// <summary>
    /// Enumerates only the players currently connected to an active run. The public branch calls
    /// this collection ConnectedPlayerIds, while newer beta branches expose PlayerIds.
    /// </summary>
    public static IReadOnlyList<ulong> GetConnectedRunPlayerNetIds(object runLobby)
    {
        ArgumentNullException.ThrowIfNull(runLobby);
        Type lobbyType = runLobby.GetType();
        foreach (string propertyName in new[] { "ConnectedPlayerIds", "PlayerIds" })
        {
            object? ids = AccessTools.Property(lobbyType, propertyName)?.GetValue(runLobby);
            if (ids != null)
                return ReadNetIds(ids, $"{lobbyType.FullName}.{propertyName}");
        }

        object players = AccessTools.Property(lobbyType, "Players")?.GetValue(runLobby)
            ?? throw new MissingMemberException(
                lobbyType.FullName,
                "ConnectedPlayerIds, PlayerIds, or Players"
            );
        return ReadPlayerNetIds(players, $"{lobbyType.FullName}.Players");
    }

    private static IReadOnlyList<ulong> ReadNetIds(object values, string source)
    {
        if (values is not System.Collections.IEnumerable sequence)
            throw new InvalidCastException($"{source} is not enumerable.");

        var netIds = new List<ulong>();
        foreach (object value in sequence)
        {
            if (value is not ulong netId)
                throw new InvalidCastException($"Could not read a UInt64 from {source}.");
            netIds.Add(netId);
        }
        return netIds;
    }

    private static IReadOnlyList<ulong> ReadPlayerNetIds(object players, string source)
    {
        if (players is not System.Collections.IEnumerable sequence)
            throw new InvalidCastException($"{source} is not enumerable.");

        var netIds = new List<ulong>();
        foreach (object player in sequence)
        {
            object? rawNetId = AccessTools.Field(player.GetType(), "id")?.GetValue(player)
                ?? AccessTools.Property(player.GetType(), "id")?.GetValue(player);
            if (rawNetId is not ulong netId)
            {
                throw new InvalidCastException(
                    $"Could not read a UInt64 from {player.GetType().FullName}.id."
                );
            }
            netIds.Add(netId);
        }
        return netIds;
    }

    /// <summary>
    /// Marks an Encounter card reward as combat-sourced on beta branches. The flag does not
    /// exist on the public branch, so resolve it by name to preserve public compatibility.
    /// </summary>
    public static CardCreationOptions WithCombatRewardCompatibility(CardCreationOptions options)
    {
        return Enum.TryParse("IsFromCombat", out CardCreationFlags isFromCombat)
            ? options.WithFlags(isFromCombat)
            : options;
    }

    /// <summary>
    /// Gets the selected local character without binding to LobbyPlayer, which was renamed
    /// to StartRunLobbyPlayer on the beta branch and changed StartRunLobby.LocalPlayer's
    /// binary return type.
    /// </summary>
    public static CharacterModel GetLocalCharacter(object lobby)
    {
        object localPlayer = GetLocalPlayer(lobby);

        // Both player types retain the same character field, so only the containing
        // player type needs to be resolved at runtime.
        return AccessTools.Field(localPlayer.GetType(), "character")?.GetValue(localPlayer) as CharacterModel
            ?? throw new InvalidCastException(
                $"Could not read a {nameof(CharacterModel)} from {localPlayer.GetType().FullName}.character."
            );
    }

    /// <summary>
    /// Gets the local lobby player's ready flag without binding to the changed
    /// StartRunLobby.LocalPlayer return type.
    /// </summary>
    public static bool IsLocalPlayerReady(object lobby)
    {
        object localPlayer = GetLocalPlayer(lobby);
        return AccessTools.Field(localPlayer.GetType(), "isReady")?.GetValue(localPlayer) as bool?
            ?? throw new InvalidCastException(
                $"Could not read a Boolean from {localPlayer.GetType().FullName}.isReady."
            );
    }

    private static object GetLocalPlayer(object lobby)
    {
        ArgumentNullException.ThrowIfNull(lobby);

        // Calling this property directly embeds its declared return type in the method
        // token, which breaks when LobbyPlayer and StartRunLobbyPlayer are exchanged.
        return AccessTools.Property(lobby.GetType(), "LocalPlayer")?.GetValue(lobby)
            ?? throw new MissingMemberException(lobby.GetType().FullName, "LocalPlayer");
    }
}
