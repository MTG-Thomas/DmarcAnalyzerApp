using DmarcAnalyzer.Api.Application.Analytics.Spf;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class SpfRecordParserTests
{
    [Fact]
    public void Parse_AllQualifier_Variants()
    {
        Assert.Equal("+", SpfRecordParser.Parse("v=spf1 +all").Terms[0].Qualifier);
        Assert.Equal("-", SpfRecordParser.Parse("v=spf1 -all").Terms[0].Qualifier);
        Assert.Equal("~", SpfRecordParser.Parse("v=spf1 ~all").Terms[0].Qualifier);
        Assert.Equal("?", SpfRecordParser.Parse("v=spf1 ?all").Terms[0].Qualifier);
        Assert.Equal("+", SpfRecordParser.Parse("v=spf1 all").Terms[0].Qualifier);
    }

    [Fact]
    public void Parse_Mechanisms_KindTargetAndCost()
    {
        var record = SpfRecordParser.Parse(
            "v=spf1 include:_spf.google.com a mx mx:mail.example.com/24 ptr ptr:example.com " +
            "exists:%{i}._spf.example.com ip4:198.51.100.0/24 ip6:2001:db8::/32 -all");

        var include = record.Terms.Single(t => t.Kind == SpfTermKind.Include);
        Assert.Equal("_spf.google.com", include.Target);
        Assert.True(include.CostsLookup);
        Assert.False(include.IsDynamic);

        var bareA = record.Terms.Single(t => t is { Kind: SpfTermKind.A, Target: null });
        Assert.True(bareA.CostsLookup);

        var mx = record.Terms.Single(t => t.Kind == SpfTermKind.Mx && t.Target == "mail.example.com");
        Assert.Equal(24, mx.Cidr4);
        Assert.Null(mx.Cidr6);

        var ptr = record.Terms.Single(t => t.Kind == SpfTermKind.Ptr && t.Target == "example.com");
        Assert.True(ptr.CostsLookup);
        Assert.False(ptr.IsDynamic);

        var exists = record.Terms.Single(t => t.Kind == SpfTermKind.Exists);
        Assert.True(exists.CostsLookup);
        Assert.True(exists.IsDynamic); // DNS existence test by design, macro or not
        Assert.True(exists.HasMacro);

        var ip4 = record.Terms.Single(t => t.Kind == SpfTermKind.Ip4);
        Assert.Equal("198.51.100.0", ip4.Target);
        Assert.Equal(24, ip4.Cidr4);
        Assert.False(ip4.CostsLookup);

        var ip6 = record.Terms.Single(t => t.Kind == SpfTermKind.Ip6);
        Assert.False(ip6.CostsLookup);

        var all = record.Terms.Single(t => t.Kind == SpfTermKind.All);
        Assert.False(all.CostsLookup);
        Assert.All(record.Terms, t => Assert.Null(t.ParseError));
    }

    [Fact]
    public void Parse_DualCidr_ParsesBothLengths()
    {
        var term = SpfRecordParser.Parse("v=spf1 mx:mail.example.com/24//64 -all")
            .Terms.Single(t => t.Kind == SpfTermKind.Mx);

        Assert.Equal("mail.example.com", term.Target);
        Assert.Equal(24, term.Cidr4);
        Assert.Equal(64, term.Cidr6);
        Assert.Null(term.ParseError);
    }

    [Fact]
    public void Parse_BareMechanismWithCidr_NeedsNoDomain()
    {
        var term = SpfRecordParser.Parse("v=spf1 a/24 -all")
            .Terms.Single(t => t.Kind == SpfTermKind.A);

        Assert.Null(term.Target);
        Assert.Equal(24, term.Cidr4);
        Assert.Null(term.ParseError);
    }

    [Fact]
    public void Parse_Modifiers_RedirectExpAndUnknown()
    {
        var record = SpfRecordParser.Parse("v=spf1 redirect=_spf.example.com exp=why.example.com foo=bar");

        var redirect = record.Terms.Single(t => t.Kind == SpfTermKind.Redirect);
        Assert.Equal("_spf.example.com", redirect.Target);
        Assert.True(redirect.CostsLookup);
        Assert.Equal("_spf.example.com", record.RedirectTarget);

        var exp = record.Terms.Single(t => t.Kind == SpfTermKind.Exp);
        Assert.False(exp.CostsLookup);
        Assert.Equal("why.example.com", record.ExpTarget);

        var unknown = record.Terms.Single(t => t.Kind == SpfTermKind.UnknownModifier);
        Assert.False(unknown.CostsLookup);
        Assert.Null(unknown.ParseError); // legal; receivers ignore it
    }

    [Fact]
    public void Parse_MacroInInclude_MarksDynamic()
    {
        var term = SpfRecordParser.Parse("v=spf1 include:%{i}._spf.example.com -all")
            .Terms.Single(t => t.Kind == SpfTermKind.Include);

        Assert.True(term.HasMacro);
        Assert.True(term.IsDynamic);
        Assert.True(term.CostsLookup); // still spends the lookup when evaluated
        Assert.Null(term.ParseError);
    }

    [Theory]
    [InlineData("INCLUDE:_spf.example.com", SpfTermKind.Include)] // mechanism names are case-insensitive
    [InlineData("MX/24", SpfTermKind.Mx)]
    [InlineData("~All", SpfTermKind.All)]
    [InlineData("Redirect=_spf.example.com", SpfTermKind.Redirect)]
    public void Parse_CaseInsensitive_NamesAndQualifiers(string token, string kind)
    {
        var term = SpfRecordParser.Parse($"v=spf1 {token}").Terms.Single();
        Assert.Equal(kind, term.Kind);
        Assert.Null(term.ParseError);
    }

    [Theory]
    [InlineData("include", "requires a domain")]
    [InlineData("include:", "requires a domain")]
    [InlineData("exists", "requires a domain")]
    [InlineData("all:example.com", "takes no arguments")]
    [InlineData("ip4:not-an-address", "not an IP address")]
    [InlineData("ip4:198.51.100.0/33", "not a /0-32 prefix")]
    [InlineData("ip6:2001:db8::/129", "not a /0-128 prefix")]
    [InlineData("mx:mail.example.com/33", "not a /0-32")]
    [InlineData("a:example.com/24//129", "not a /0-32//0-128")]
    [InlineData("frobnicate:example.com", "Unknown mechanism")]
    [InlineData("redirect=", "redirect= with no domain")]
    public void Parse_MalformedTerm_KeepsTermWithError(string token, string expectedFragment)
    {
        var record = SpfRecordParser.Parse($"v=spf1 {token} -all");

        var bad = record.Terms.Single(t => t.Text == token);
        Assert.Equal(SpfTermKind.Invalid, bad.Kind);
        Assert.Contains(expectedFragment, bad.ParseError, StringComparison.Ordinal);
        Assert.False(bad.CostsLookup);

        // The record survives around the bad term.
        Assert.Equal(2, record.Terms.Count);
        Assert.Null(record.Terms.Single(t => t.Kind == SpfTermKind.All).ParseError);
    }

    [Fact]
    public void Parse_PreservesTermOrder()
    {
        var record = SpfRecordParser.Parse("v=spf1 ip4:198.51.100.0/24 include:a.example.com mx -all");

        Assert.Equal(
            ["ip4:198.51.100.0/24", "include:a.example.com", "mx", "-all"],
            record.Terms.Select(t => t.Text));
    }

    [Fact]
    public void Parse_TopLevelLookupCount_MatchesLegacyCounter()
    {
        // The recursive walk must agree with ParseSpf on what costs a lookup at
        // the top level — fixtures both counters must score identically.
        const string record = "v=spf1 include:_spf.google.com include:sendgrid.net a mx ip4:198.51.100.10 -all";

        var parsed = SpfRecordParser.Parse(record);
        Assert.Equal(4, parsed.Terms.Count(t => t.CostsLookup));
    }
}
