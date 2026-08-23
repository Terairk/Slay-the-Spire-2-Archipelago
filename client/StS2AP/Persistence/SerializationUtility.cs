using MegaCrit.Sts2.Core.Saves.Managers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using StS2AP.Utils;

namespace StS2AP.Persistence;

public static class SerializationUtility
{
    public static JsonSerializerOptions CombinedOptions { get; }

    static SerializationUtility()
    {
        LogUtility.Info("Getting assembly");
        var megaAssembly = typeof(RunSaveManager).Assembly;
        LogUtility.Info("Getting megaContext");
        Type? contextType = megaAssembly.GetType(
            "MegaCrit.Sts2.Core.Saves.MegaCritSerializerContext"
        );
        LogUtility.Info("Getting Default");
        var defaultProperty = contextType?.GetProperty(
            "Default",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static
        );
        var defaultField = contextType?.GetField(
            "Default",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static
        );
        LogUtility.Info("Getting Dereferencing Default");
        var megaResolver = (IJsonTypeInfoResolver?)(
            defaultProperty?.GetValue(null) ?? defaultField?.GetValue(null)
        ) ?? throw new InvalidOperationException(
            "Could not resolve MegaCritSerializerContext.Default."
        );

        LogUtility.Info("Getting Options");
        JsonSerializerOptions megaOptions = (megaResolver as JsonSerializerContext)?.Options
            ?? throw new InvalidOperationException(
                "MegaCritSerializerContext.Default did not expose serializer options."
            );

        CombinedOptions = new JsonSerializerOptions(megaOptions)
        {
            TypeInfoResolver = JsonTypeInfoResolver.Combine(
                megaResolver,
                ApSerializationContext.Default
            ),
        };
    }
}
