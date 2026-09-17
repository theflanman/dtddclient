using System.Text.Json;
using DoesTheDogDie.Api;
using DoesTheDogDie.Tests.Support;

namespace DoesTheDogDie.Tests.Api;

public class ModelSerializationTests
{
    [Fact]
    public void Search_Deserializes()
    {
        var items = JsonSerializer.Deserialize(Fixtures.Read("search.json"), DtddJsonContext.Default.ListItem)!;

        var item = Assert.Single(items);
        Assert.Equal(10752, item.Id);
        Assert.Equal("Old Yeller", item.Name);
        Assert.Equal(["adventure"], item.Genres);
        Assert.Equal(1957, item.ReleaseYear);
        Assert.Equal(15, item.ItemTypeId);
        Assert.Equal("Movie", item.ItemTypeName);
        Assert.Equal(22660, item.TmdbId);
        Assert.Equal("tt0050798", item.ImdbId);
    }

    [Fact]
    public void Item_Deserializes()
    {
        var item = JsonSerializer.Deserialize(Fixtures.Read("item.json"), DtddJsonContext.Default.ItemDetail)!;

        Assert.Equal(10752, item.Id);
        Assert.Equal("Old Yeller", item.Name);
        Assert.Single(item.TopicItemStats);
        Assert.Equal(57, item.TopicItemStats[0].YesSum);
        Assert.Equal(3, item.TopicItemStats[0].NoSum);
        Assert.Equal(7, item.TopicItemStats[0].NumComments);
        Assert.Equal(153, item.TopicItemStats[0].TopicId);
        Assert.Equal("a dog dies", item.TopicItemStats[0].TopicName);
        Assert.Equal(10752, item.TopicItemStats[0].ItemId);
    }

    [Fact]
    public void Ratings_Deserialize()
    {
        var ratings = JsonSerializer.Deserialize(Fixtures.Read("ratings.json"), DtddJsonContext.Default.ListRating)!;

        var rating = Assert.Single(ratings);
        Assert.Equal(3369418, rating.Id);
        Assert.Equal(1, rating.Yes);
        Assert.Equal(0, rating.No);
        Assert.Equal(0, rating.VoteSum);
        Assert.Equal("The dog is shot by his owner...", rating.TriggerDescription);
        Assert.False(rating.IsRampant);
        Assert.Equal(-1, rating.Index1);
        Assert.Equal(-1, rating.Index2);
        Assert.Equal(1, rating.Position1);
        Assert.Equal(18, rating.Position2);
        Assert.Equal(42, rating.Position3);
        Assert.Equal(1, rating.SafePosition1);
        Assert.Equal(20, rating.SafePosition2);
        Assert.Equal(5, rating.SafePosition3);
        Assert.Equal("Travis picks up the gun.", rating.CueDescription);
        Assert.Equal(10752, rating.ItemId);
        Assert.Equal(153, rating.TopicId);
        Assert.True(rating.IsSceneAlert);
    }

    [Fact]
    public void Topics_Deserialize()
    {
        var topics = JsonSerializer.Deserialize(Fixtures.Read("topics.json"), DtddJsonContext.Default.ListTopic)!;

        var topic = Assert.Single(topics);
        Assert.Equal(153, topic.Id);
        Assert.Equal("a dog dies", topic.Name);
        Assert.Equal("no dogs die", topic.NotName);
        Assert.Equal("Dog death, canine death...", topic.Keywords);
        Assert.Equal("...", topic.Description);
        Assert.Equal("Does the dog die", topic.DoesName);
        Assert.Equal("where the dog dies", topic.ListName);
        Assert.Equal("dogs dying", topic.MinimalName);
        Assert.Equal(56, topic.TopicCategoryId);
        Assert.Equal(22, topic.AltTopicCategoryId);
    }

    [Fact]
    public void ItemTypes_Deserialize()
    {
        var itemTypes = JsonSerializer.Deserialize(Fixtures.Read("itemtypes.json"), DtddJsonContext.Default.ListItemType)!;

        Assert.Equal(3, itemTypes.Count);
        var movie = itemTypes[0];
        Assert.Equal(15, movie.Id);
        Assert.Equal("Movie", movie.Name);
        Assert.Equal("movie", movie.Slug);
        Assert.Equal("watch", movie.Verb);
        Assert.Equal("watched", movie.PastTenseVerb);
        Assert.Null(movie.Index1Label);
        Assert.Null(movie.Index2Label);
        Assert.Equal("Hours", movie.Position1Label);

        var tv = itemTypes[1];
        Assert.Equal("Season", tv.Index1Label);
        Assert.Equal("Episode", tv.Index2Label);
    }

    [Fact]
    public void TopicCategories_Deserialize()
    {
        var categories = JsonSerializer.Deserialize(
            Fixtures.Read("topiccategories.json"), DtddJsonContext.Default.ListTopicCategory)!;

        Assert.Equal(2, categories.Count);
        Assert.Equal(56, categories[0].Id);
        Assert.Equal("Animal Injury or Death", categories[0].Name);
        Assert.Equal(3, categories[0].TopicSuperCategoryId);
    }

    [Fact]
    public void TopicSuperCategories_Deserialize()
    {
        var superCategories = JsonSerializer.Deserialize(
            Fixtures.Read("topicsupercategories.json"), DtddJsonContext.Default.ListTopicSuperCategory)!;

        Assert.Equal(2, superCategories.Count);
        Assert.Equal(2, superCategories[0].Id);
        Assert.Equal("Emotional Spoilers", superCategories[0].Name);
        Assert.Equal("Emotional", superCategories[0].ShortName);
    }

    [Fact]
    public void Error_Deserializes()
    {
        var error = JsonSerializer.Deserialize(Fixtures.Read("error.json"), DtddJsonContext.Default.ApiError)!;

        Assert.Equal("not_found", error.Error);
        Assert.Equal("Item, topic or rating does not exist", error.Message);
    }

    [Fact]
    public void UnknownMembers_AreIgnored()
    {
        const string json = """
            {
              "id": 1,
              "name": "Test Item",
              "itemTypeId": 15,
              "itemTypeName": "Movie",
              "someBrandNewField": "should be ignored",
              "nested": { "also": "ignored" }
            }
            """;

        var item = JsonSerializer.Deserialize(json, DtddJsonContext.Default.Item)!;

        Assert.Equal(1, item.Id);
        Assert.Equal("Test Item", item.Name);
    }

    [Fact]
    public void MissingOptional_IsNull()
    {
        const string json = """
            {
              "id": 1,
              "name": "Test Item",
              "itemTypeId": 15,
              "itemTypeName": "Movie"
            }
            """;

        var item = JsonSerializer.Deserialize(json, DtddJsonContext.Default.Item)!;

        Assert.Null(item.ImdbId);
    }
}
