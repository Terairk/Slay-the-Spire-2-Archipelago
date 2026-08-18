using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace StS2AP.Utils;

/// <summary>
/// Bridges game API differences between the public 0.107.1 branch and newer beta branches.
/// Keep direct references to renamed or removed game types inside this reflection boundary.
/// </summary>
public static class BetaMainCompatibility
{
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
