using Newtonsoft.Json.Linq;

namespace StS2AP.Utils;

internal static class ApSlotData
{
    internal static Dictionary<string, object> Normalize(IReadOnlyDictionary<string, object> slotData) =>
        slotData.ToDictionary(pair => pair.Key, pair => NormalizeValue(pair.Value));

    private static object NormalizeValue(object value)
    {
        // Keep null, numbers, booleans and strings as they are. JSON objects and
        // arrays also need no conversion if they already use our Newtonsoft DLL.
        if (value is null or IConvertible or JToken)
            return value!;

        // MultiClient's remaining values are JSON objects or arrays from another
        // loaded copy of Newtonsoft. Ask that object to write its JSON, then read
        // the text with our copy so our JObject/JArray checks will work.
        // Do not use JsonConvert.SerializeObject(value) here: in the separate-DLL
        // test it wrote {"locked":true} as {"locked":[]} and lost the value.
        return JToken.Parse(value.ToString()!);
    }
}
