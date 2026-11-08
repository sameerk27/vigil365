using System.Text.Json;
using M365SecurityDashboard.Api.Services.Common;
using Xunit;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// Microsoft re-types and drops fields without notice. These lock the promise that a reader
/// never throws: a surprise in one record must not turn a whole section into an error.
/// </summary>
public class JsonReadTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Reads_a_nested_string()
    {
        var e = Parse("""{"location":{"city":"Leeds"}}""");
        Assert.Equal("Leeds", JsonRead.Str(e, "location", "city"));
    }

    [Theory]
    [InlineData("""{}""")]                               // missing
    [InlineData("""{"location":null}""")]                // null step
    [InlineData("""{"location":"Leeds"}""")]             // step is a string, not an object
    [InlineData("""{"location":["a"]}""")]               // step is an array
    [InlineData("""{"location":{"city":42}}""")]         // wrong type at the leaf
    [InlineData("""{"location":{"city":{"name":"x"}}}""")]
    public void A_missing_or_wrongly_typed_path_is_null_not_an_exception(string json)
    {
        var e = Parse(json);
        Assert.Null(JsonRead.Str(e, "location", "city"));
        Assert.Null(JsonRead.Date(e, "location", "city"));
        Assert.Empty(JsonRead.Strings(e, "location", "city"));
    }

    [Fact]
    public void A_non_object_root_is_null_not_an_exception()
    {
        Assert.Null(JsonRead.Str(Parse("""[1,2]"""), "id"));
        Assert.Null(JsonRead.Str(Parse("""null"""), "id"));
    }

    [Fact]
    public void Name_matching_is_exact_unless_asked_to_ignore_case()
    {
        var e = Parse("""{"Properties":{"DisplayName":"Flow"}}""");
        Assert.Null(JsonRead.Str(e, "properties", "displayName"));
        Assert.Equal("Flow", JsonRead.NonBlankIgnoreCase(e, "properties", "displayName"));
        Assert.NotNull(JsonRead.GetIgnoreCase(e, "PROPERTIES"));
    }

    [Theory]
    [InlineData("""{"name":""}""")]
    [InlineData("""{"name":"   "}""")]
    public void NonBlank_treats_empty_and_whitespace_as_absent(string json)
    {
        var e = Parse(json);
        Assert.Null(JsonRead.NonBlank(e, "name"));
        Assert.Null(JsonRead.NonBlankIgnoreCase(e, "NAME"));
        Assert.NotNull(JsonRead.Str(e, "name"));
    }

    [Fact]
    public void Text_returns_scalars_as_their_json_text()
    {
        var e = Parse("""{"s":"x","n":12.5,"t":true,"f":false,"o":{},"z":null}""");
        Assert.Equal("x", JsonRead.Text(e, "s"));
        Assert.Equal("12.5", JsonRead.Text(e, "n"));
        Assert.Equal("true", JsonRead.Text(e, "t"));
        Assert.Equal("false", JsonRead.Text(e, "f"));
        Assert.Null(JsonRead.Text(e, "o"));
        Assert.Null(JsonRead.Text(e, "z"));
    }

    [Fact]
    public void IsTrue_only_for_a_json_true()
    {
        var e = Parse("""{"a":true,"b":"true","c":1,"d":false}""");
        Assert.True(JsonRead.IsTrue(e, "a"));
        Assert.False(JsonRead.IsTrue(e, "b"));
        Assert.False(JsonRead.IsTrue(e, "c"));
        Assert.False(JsonRead.IsTrue(e, "d"));
        Assert.False(JsonRead.IsTrue(e, "missing"));
    }

    [Fact]
    public void Numbers_are_read_only_from_json_numbers()
    {
        var e = Parse("""{"i":7,"big":9999999999,"d":1.5,"s":"7"}""");
        Assert.Equal(7, JsonRead.Int(e, "i"));
        Assert.Null(JsonRead.Int(e, "big"));      // out of Int32 range
        Assert.Null(JsonRead.Int(e, "d"));
        Assert.Null(JsonRead.Int(e, "s"));
        Assert.Equal(1.5, JsonRead.Number(e, "d"));
        Assert.Null(JsonRead.Number(e, "s"));
    }

    [Fact]
    public void A_date_without_an_offset_is_taken_as_utc()
    {
        var e = Parse("""{"at":"2026-10-04T07:47:08","z":"2026-10-04T07:47:08+02:00","bad":"yesterday"}""");
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 7, 47, 8, TimeSpan.Zero), JsonRead.Date(e, "at"));
        Assert.Equal(TimeSpan.FromHours(2), JsonRead.Date(e, "z")!.Value.Offset);
        Assert.Null(JsonRead.Date(e, "bad"));
    }

    [Fact]
    public void Strings_keeps_only_non_empty_strings()
    {
        var e = Parse("""{"t":["T1078",null,"",3,"T1110",{}]}""");
        Assert.Equal(new[] { "T1078", "T1110" }, JsonRead.Strings(e, "t"));
    }
}
