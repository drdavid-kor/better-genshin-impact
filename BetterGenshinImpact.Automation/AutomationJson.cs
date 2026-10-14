using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace BetterGenshinImpact.Automation;

public static class AutomationJson
{
    private static JsonSerializerSettings Settings => new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        NullValueHandling = NullValueHandling.Ignore,
        TypeNameHandling = TypeNameHandling.None,
        MaxDepth = 32,
        DateParseHandling = DateParseHandling.None
    };

    public static string Serialize(object value) => JsonConvert.SerializeObject(value, Settings);

    public static T Deserialize<T>(string json)
    {
        using var input = new StringReader(json);
        using var reader = new JsonTextReader(input) { MaxDepth = 32, DateParseHandling = DateParseHandling.None };
        var token = JToken.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (reader.Read()) throw new JsonException("JSON 文档尾部存在多余内容。");
        return token.ToObject<T>(JsonSerializer.Create(Settings)) ?? throw new JsonException("JSON 文档不能为空。");
    }

    public static JObject ToObject(object value) => Deserialize<JObject>(Serialize(value));
}
