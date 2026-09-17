using System.Text.Json.Serialization;

namespace DoesTheDogDie.Api;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Item))]
[JsonSerializable(typeof(ItemDetail))]
[JsonSerializable(typeof(TopicItemStat))]
[JsonSerializable(typeof(Rating))]
[JsonSerializable(typeof(Topic))]
[JsonSerializable(typeof(ItemType))]
[JsonSerializable(typeof(TopicCategory))]
[JsonSerializable(typeof(TopicSuperCategory))]
[JsonSerializable(typeof(ApiError))]
[JsonSerializable(typeof(List<Item>))]
[JsonSerializable(typeof(List<Rating>))]
[JsonSerializable(typeof(List<Topic>))]
[JsonSerializable(typeof(List<ItemType>))]
[JsonSerializable(typeof(List<TopicCategory>))]
[JsonSerializable(typeof(List<TopicSuperCategory>))]
internal sealed partial class DtddJsonContext : JsonSerializerContext
{
}
