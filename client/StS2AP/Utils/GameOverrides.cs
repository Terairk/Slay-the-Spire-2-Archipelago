using System.Reflection;
using HarmonyLib;

namespace StS2AP.Utils;

/// <summary>Finds the game's own overrides of a model method, for patches that target all of them.</summary>
internal static class GameOverrides
{
    /// <summary>
    /// Returns every override of <paramref name="methodName"/> declared in the game assembly by a
    /// subclass of <paramref name="baseType"/>, including overrides on abstract intermediate
    /// classes. Mod types are not included.
    /// </summary>
    public static IEnumerable<MethodBase> Find(Type baseType, string methodName, params Type[] parameters) =>
        AccessTools.GetTypesFromAssembly(baseType.Assembly)
            .Where(type => type.IsSubclassOf(baseType) && !type.ContainsGenericParameters)
            .Select(type => AccessTools.DeclaredMethod(type, methodName, parameters))
            .Where(method => method is { IsAbstract: false })
            .Cast<MethodBase>();
}
