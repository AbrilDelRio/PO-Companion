using CclWebApi.Helpers;
using CclWebApi.Models;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CclWebApiCore.Tests;

public class HelperTests
{
    private class Poco
    {
        public string? Name { get; set; }
        public int Value { get; set; }
    }

    [Fact]
    public void GetCrmConnString_ReadsCRMConnectionKey()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CRMConnection"] = "url=https://example.crm.dynamics.com"
            })
            .Build();

        Assert.Equal("url=https://example.crm.dynamics.com", Helper.GetCrmConnString(config));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetListFromJson_NullOrWhitespace_ReturnsEmptyList(string? json)
    {
        var result = Helper.GetListFromJson<Poco>(json!);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void GetListFromJson_ValidJson_Deserializes()
    {
        var result = Helper.GetListFromJson<Poco>("[{\"Name\":\"a\",\"Value\":1},{\"Name\":\"b\",\"Value\":2}]");

        Assert.Equal(2, result.Count);
        Assert.Equal("a", result[0].Name);
        Assert.Equal(2, result[1].Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void GetObjectFromJson_NullOrEmpty_ReturnsNewInstance(string? json)
    {
        var result = Helper.GetObjectFromJson<Poco>(json!);

        Assert.NotNull(result);
        Assert.Null(result.Name);
        Assert.Equal(0, result.Value);
    }

    [Fact]
    public void GetObjectFromJson_ValidJson_Deserializes()
    {
        var result = Helper.GetObjectFromJson<Poco>("{\"Name\":\"x\",\"Value\":5}");

        Assert.Equal("x", result.Name);
        Assert.Equal(5, result.Value);
    }

    [Fact]
    public void GetJsonFromList_NullList_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, Helper.GetJsonFromList<Poco>(null!));
    }

    [Fact]
    public void GetJsonFromList_EmptyList_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, Helper.GetJsonFromList(new List<Poco>()));
    }

    [Fact]
    public void GetJsonFromList_ThenGetListFromJson_RoundTrips()
    {
        var original = new List<Poco> { new() { Name = "a", Value = 1 } };

        var json = Helper.GetJsonFromList(original);
        var back = Helper.GetListFromJson<Poco>(json);

        Assert.Single(back);
        Assert.Equal("a", back[0].Name);
        Assert.Equal(1, back[0].Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetDayOffParametersFromJson_NullOrWhitespace_ReturnsNewInstance(string? json)
    {
        Assert.NotNull(Helper.GetDayOffParemetersFromJson(json!));
    }

    [Fact]
    public void GetSessionTrackList_Null_ReturnsNull()
    {
        Assert.Null(Helper.GetSessionTrackListFromSessionUploadInfoList(null!));
    }

    [Fact]
    public void GetSessionTrackList_Empty_ReturnsNull()
    {
        Assert.Null(Helper.GetSessionTrackListFromSessionUploadInfoList(new List<SessionUploadInfo>()));
    }

    [Fact]
    public void GetSessionTrackList_MapsFieldsAndParsesTransactionType()
    {
        var source = new List<SessionUploadInfo>
        {
            new()
            {
                SessionTrackId = "ST-1",
                ContactFirstName = "Jane",
                ContactLastName = "Doe",
                TransactionType = "Transfer"
            }
        };

        var result = Helper.GetSessionTrackListFromSessionUploadInfoList(source);

        Assert.NotNull(result);
        var track = Assert.Single(result);
        Assert.Equal("ST-1", track.SessionTrackID);
        Assert.Equal("Jane", track.Contact.FirstName);
        Assert.Equal("Doe", track.Contact.LastName);
        Assert.Equal(MagentoTransactionType.Transfer, track.SessionRegistration.TransactionType);
    }
}
