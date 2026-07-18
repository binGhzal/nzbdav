using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using NzbWebDAV.Extensions;

namespace backend.Tests.Extensions;

public sealed class HttpContextExtensionsTests
{
    [Fact]
    public void GetInternalRequestApiKey_ReturnsSingleHeaderOnly()
    {
        var context = Context();
        context.Request.Headers["x-api-key"] = "internal-key";
        context.Request.QueryString = new QueryString("?apikey=external-key");
        context.Request.Form = Form(("apiKey", "provider-key"));

        Assert.Equal("internal-key", context.GetInternalRequestApiKey());
    }

    [Fact]
    public void GetInternalRequestApiKey_DoesNotTreatProviderApiKeyFormAsAuthentication()
    {
        var context = Context();
        context.Request.Form = Form(("apiKey", "provider-key"));

        Assert.Null(context.GetInternalRequestApiKey());
    }

    [Fact]
    public void GetInternalRequestApiKey_RejectsRepeatedHeaders()
    {
        var context = Context();
        context.Request.Headers["x-api-key"] = new StringValues(["one", "two"]);

        Assert.Throws<BadHttpRequestException>(() => context.GetInternalRequestApiKey());
    }

    [Theory]
    [InlineData("header")]
    [InlineData("query")]
    [InlineData("form")]
    public void GetProtocolRequestApiKey_AcceptsEachCanonicalCarrier(string carrier)
    {
        var context = Context();
        SetCarrier(context, carrier, "public-key");

        Assert.Equal("public-key", context.GetProtocolRequestApiKey());
    }

    [Fact]
    public void GetProtocolRequestApiKey_AcceptsIdenticalHeaderAndQueryForAiostreams()
    {
        var context = Context();
        context.Request.Headers["x-api-key"] = "public-key";
        context.Request.QueryString = new QueryString("?mode=queue&apikey=public-key");

        Assert.Equal("public-key", context.GetProtocolRequestApiKey());
    }

    [Fact]
    public void GetProtocolRequestApiKey_AcceptsIdenticalHeaderQueryAndForm()
    {
        var context = Context();
        context.Request.Headers["x-api-key"] = "public-key";
        context.Request.QueryString = new QueryString("?apikey=public-key");
        context.Request.Form = Form(("apikey", "public-key"));

        Assert.Equal("public-key", context.GetProtocolRequestApiKey());
    }

    [Theory]
    [InlineData("header-query")]
    [InlineData("header-form")]
    [InlineData("query-form")]
    public void GetProtocolRequestApiKey_RejectsConflictingCarriers(string conflict)
    {
        var context = Context();
        switch (conflict)
        {
            case "header-query":
                context.Request.Headers["x-api-key"] = "one";
                context.Request.QueryString = new QueryString("?apikey=two");
                break;
            case "header-form":
                context.Request.Headers["x-api-key"] = "one";
                context.Request.Form = Form(("apikey", "two"));
                break;
            case "query-form":
                context.Request.QueryString = new QueryString("?apikey=one");
                context.Request.Form = Form(("apikey", "two"));
                break;
        }

        Assert.Throws<BadHttpRequestException>(() => context.GetProtocolRequestApiKey());
    }

    [Theory]
    [InlineData("?apikey=one&apikey=one")]
    [InlineData("?apikey=one&apikey=two")]
    public void GetProtocolRequestApiKey_RejectsRepeatedQueryCarrier(string query)
    {
        var context = Context();
        context.Request.QueryString = new QueryString(query);

        Assert.Throws<BadHttpRequestException>(() => context.GetProtocolRequestApiKey());
    }

    [Fact]
    public void GetProtocolRequestApiKey_RejectsRepeatedFormCarrier()
    {
        var context = Context();
        context.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["apikey"] = new StringValues(["one", "one"])
        });

        Assert.Throws<BadHttpRequestException>(() => context.GetProtocolRequestApiKey());
    }

    [Theory]
    [InlineData("apiKey")]
    [InlineData("APIKEY")]
    public void GetProtocolRequestApiKey_RejectsNonCanonicalQueryCarrierName(string key)
    {
        var context = Context();
        context.Request.QueryString = new QueryString($"?{key}=public-key");

        Assert.Throws<BadHttpRequestException>(() => context.GetProtocolRequestApiKey());
    }

    [Theory]
    [InlineData("apiKey")]
    [InlineData("APIKEY")]
    public void GetProtocolRequestApiKey_RejectsNonCanonicalFormCarrierName(string key)
    {
        var context = Context();
        context.Request.Form = Form((key, "public-key"));

        Assert.Throws<BadHttpRequestException>(() => context.GetProtocolRequestApiKey());
    }

    [Theory]
    [InlineData("header")]
    [InlineData("query")]
    [InlineData("form")]
    public void GetProtocolRequestApiKey_RejectsEmptyCarrier(string carrier)
    {
        var context = Context();
        SetCarrier(context, carrier, "");

        Assert.Throws<BadHttpRequestException>(() => context.GetProtocolRequestApiKey());
    }

    [Theory]
    [InlineData("header")]
    [InlineData("query")]
    [InlineData("form")]
    public void GetProtocolRequestApiKey_RejectsOversizedCarrier(string carrier)
    {
        var context = Context();
        SetCarrier(context, carrier, new string('a', 513));

        Assert.Throws<BadHttpRequestException>(() => context.GetProtocolRequestApiKey());
    }

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Form = new FormCollection(new Dictionary<string, StringValues>());
        return context;
    }

    private static FormCollection Form(params (string Key, string Value)[] entries)
    {
        return new FormCollection(entries.ToDictionary(
            entry => entry.Key,
            entry => new StringValues(entry.Value),
            StringComparer.Ordinal));
    }

    private static void SetCarrier(DefaultHttpContext context, string carrier, string value)
    {
        switch (carrier)
        {
            case "header":
                context.Request.Headers["x-api-key"] = value;
                break;
            case "query":
                context.Request.QueryString = new QueryString($"?apikey={Uri.EscapeDataString(value)}");
                break;
            case "form":
                context.Request.Form = Form(("apikey", value));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(carrier));
        }
    }
}
