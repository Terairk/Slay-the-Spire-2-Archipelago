using MegaCrit.Sts2.Core.Entities.Players;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using StS2AP.Models;
using StS2AP.Multiplayer;

namespace StS2AP.Extensions
{
    public static class PlayerExtensions
    {
        /// <summary>
        /// Returns the name of the current character, as their name appears in the Archipelago's APWorld.
        /// </summary>
        /// <example>An Ironclad instance returns "Ironclad", because items for that character include "Ironclad Card Reward", "Ironclad Relic", etc.</example>
        public static string APName(this Player player)
        {
            if (ApPlayerContextResolver.TryGetApCharacterName(
                    player,
                    out string name
                ))
            {
                return name;
            }

            string internalName = player.getInternalName();
            LogUtility.Warn(
                $"Could not resolve AP character name for player {player.NetId} "
                    + $"with character id '{internalName}'"
            );
            return internalName;
        }

        /// <summary>
        /// Returns this player's AP character offset using that player's multiplayer AP context.
        /// </summary>
        public static long? GetCharacterOffset(this Player player)
        {
            if (ApPlayerContextResolver.TryGetCharacterConfig(
                    player,
                    out CharacterConfig config
                ))
            {
                return config.CharOffset;
            }

            LogUtility.Warn(
                $"Could not resolve AP character offset for player {player.NetId} "
                    + $"with character id '{player.getInternalName()}'"
            );
            return null;
        }

        /// <summary>
        /// What the game thinks the character's name is.
        /// </summary>
        public static string getInternalName(this Player player)
        {
            return player.Character.Id.Entry;
        }
    }
}
