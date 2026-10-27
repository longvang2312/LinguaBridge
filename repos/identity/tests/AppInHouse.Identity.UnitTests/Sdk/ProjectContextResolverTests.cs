using AppInHouse.Identity.ClientSdk;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace AppInHouse.Identity.UnitTests.Sdk;

public class ProjectContextResolverTests
{
    private static ProjectContextResolver Create(Action<AppInHouseAuthorizationOptions>? configure = null)
    {
        var options = new AppInHouseAuthorizationOptions();
        configure?.Invoke(options);
        return new ProjectContextResolver(Options.Create(options));
    }

    private static DefaultHttpContext Request(string? header = null, string? route = null)
    {
        var context = new DefaultHttpContext();
        if (header is not null)
            context.Request.Headers["X-Project-Code"] = header;
        if (route is not null)
            context.Request.RouteValues = new RouteValueDictionary { ["projectCode"] = route };

        return context;
    }

    [Fact]
    public void Header_IsUsed()
        => Assert.Equal("ERP", Create().Resolve(Request(header: "ERP")));

    [Fact]
    public void RouteValue_IsUsed()
        => Assert.Equal("ERP", Create().Resolve(Request(route: "ERP")));

    [Fact]
    public void Value_IsTrimmed()
        => Assert.Equal("ERP", Create().Resolve(Request(header: "  ERP ")));

    [Fact]
    public void NoProject_ResolvesToNull()
        => Assert.Null(Create().Resolve(Request()));

    [Fact]
    public void HeaderAndRoute_MustAgree_IgnoringCase()
    {
        Assert.Equal("ERP", Create().Resolve(Request(header: "erp", route: "ERP")));
        Assert.Null(Create().Resolve(Request(header: "ERP", route: "CRM")));
    }

    [Fact]
    public void RepeatedHeader_IsAmbiguous()
    {
        var context = Request();
        context.Request.Headers["X-Project-Code"] = new[] { "ERP", "CRM" };

        Assert.Null(Create().Resolve(context));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ERP:admin")]
    [InlineData("ERP/../CRM")]
    [InlineData("ERP\r\nX-Evil: 1")]
    [InlineData("a;DROP TABLE")]
    public void MalformedHeader_ResolvesToNull(string value)
        => Assert.Null(Create().Resolve(Request(header: value)));

    [Fact]
    public void TooLongCode_ResolvesToNull()
    {
        Assert.Null(Create().Resolve(Request(header: new string('a', 51))));
        Assert.NotNull(Create().Resolve(Request(header: new string('a', 50))));
    }

    [Fact]
    public void CodesWithSpacesDotsAndHyphens_AreAllowed()
        => Assert.Equal("Project A-1.x_y", Create().Resolve(Request(header: "Project A-1.x_y")));

    [Fact]
    public void MalformedRouteValue_ResolvesToNull_EvenWithAValidHeader()
        => Assert.Null(Create().Resolve(Request(header: "ERP", route: "ERP:1")));

    [Fact]
    public void CustomHeaderAndRouteKey_AreHonoured()
    {
        var resolver = Create(o =>
        {
            o.ProjectCodeHeaderName = "X-Tenant";
            o.ProjectCodeRouteKey = "tenant";
        });
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Tenant"] = "ERP";

        Assert.Equal("ERP", resolver.Resolve(context));

        context = new DefaultHttpContext();
        context.Request.RouteValues = new RouteValueDictionary { ["tenant"] = "CRM" };
        Assert.Equal("CRM", resolver.Resolve(context));
    }
}
