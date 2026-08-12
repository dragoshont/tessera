using System.Text.Json;
using Tessera.Core.Product;
using Xunit;

namespace Tessera.Core.Tests;

public sealed class ProductContentValidationTests
{
    [Fact]
    public void Text_rejects_unlabeled_credential_families()
    {
        var values = new[]
        {
            "gh" + "p_" + new string('1', 36),
            "github_" + "pat_" + "11AA22BB33CC44DD55EE66FF77GG88",
            "sk" + "-1234567890abcdefghijklmnop",
            "eyJhbGciOiJIUzI1NiJ9" + ".eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcdefghijklmnop",
            "xox" + "b-123456789012-abcdefghijklmnopqrstuv",
        };
        foreach (var value in values)
            Assert.Throws<ArgumentException>(()=>ProductContentValidation.Text(value,"providerOutput"));
    }

    [Fact]
    public void Json_rejects_nested_unlabeled_credential_values()
    {
        using var document=JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            result = new { note = "github_" + "pat_" + "11AA22BB33CC44DD55EE66FF77GG88" },
        }));
        Assert.Throws<ArgumentException>(()=>ProductContentValidation.Json(document.RootElement,"providerOutput"));
    }

    [Theory]
    [InlineData("token")]
    [InlineData("secret")]
    [InlineData("authToken")]
    [InlineData("bearer_token")]
    [InlineData("personalAccessToken")]
    public void Json_rejects_generic_nested_credential_property_names(string property)
    {
        using var document=JsonDocument.Parse(JsonSerializer.Serialize(new{result=new Dictionary<string,string>{{property,"opaque-credential-value"}}}));
        Assert.Throws<ArgumentException>(()=>ProductContentValidation.Json(document.RootElement,"providerOutput"));
    }

    [Fact]
    public void Text_allows_normal_product_identifiers()
    {
        Assert.Equal("issue gh-123 and model sketched-v2",ProductContentValidation.Text("issue gh-123 and model sketched-v2","text"));
    }
}