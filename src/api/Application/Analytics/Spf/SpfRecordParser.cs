namespace DmarcAnalyzer.Api.Application.Analytics.Spf;

/// <summary>Term kinds in an SPF record (RFC 7208 §5-6), plus malformed and ignored forms.</summary>
public static class SpfTermKind
{
    public const string All = "all";
    public const string Include = "include";
    public const string A = "a";
    public const string Mx = "mx";
    public const string Ptr = "ptr";
    public const string Exists = "exists";
    public const string Ip4 = "ip4";
    public const string Ip6 = "ip6";
    public const string Redirect = "redirect";
    public const string Exp = "exp";

    /// <summary>name=value that is neither redirect nor exp — legal, and ignored by receivers.</summary>
    public const string UnknownModifier = "unknown_modifier";

    /// <summary>Not parseable as any term — receivers permerror when evaluation reaches it.</summary>
    public const string Invalid = "invalid";
}

/// <summary>
/// One parsed SPF term, in published order. Pure syntax — whether the term matches
/// anything is evaluation, which needs a sender IP this analysis never has.
/// </summary>
public sealed record SpfTerm(
    /// <summary>The term exactly as published.</summary>
    string Text,
    string Kind,
    /// <summary>One of + - ~ ?. Default + when the term carries none.</summary>
    string Qualifier,
    /// <summary>domain-spec for include/redirect/a/mx/ptr/exists, address for ip4/ip6, else null.</summary>
    string? Target,
    int? Cidr4,
    int? Cidr6,
    /// <summary>Target contains a %{macro} — unexpandable without a sender identity.</summary>
    bool HasMacro,
    /// <summary>Counts toward the RFC 7208 §4.6.4 limit of 10 DNS-causing mechanisms.</summary>
    bool CostsLookup,
    /// <summary>Authorization set unknowable statically: macros, or exists: (DNS-by-design).</summary>
    bool IsDynamic,
    /// <summary>Why the term did not parse, null when it did.</summary>
    string? ParseError);

/// <summary>An SPF record split into ordered terms. Malformed terms stay in place with a ParseError rather than dropping the record.</summary>
public sealed record SpfRecord(
    string Raw,
    IReadOnlyList<SpfTerm> Terms,
    /// <summary>redirect= target when present (at most one is meaningful).</summary>
    string? RedirectTarget,
    /// <summary>exp= target when present. Explanations cost nothing toward the lookup budget.</summary>
    string? ExpTarget);

/// <summary>
/// Splits an SPF record into terms per RFC 7208 §5 (mechanisms) and §6 (modifiers).
/// Static and side-effect free so tests can feed it records directly.
/// </summary>
public static class SpfRecordParser
{
    public static SpfRecord Parse(string raw)
    {
        var terms = new List<SpfTerm>();
        string? redirect = null;
        string? exp = null;

        var tokens = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens.Skip(1))
        {
            var term = ParseTerm(token);
            terms.Add(term);

            if (term.Kind == SpfTermKind.Redirect && redirect is null)
            {
                redirect = term.Target;
            }

            if (term.Kind == SpfTermKind.Exp && exp is null)
            {
                exp = term.Target;
            }
        }

        return new SpfRecord(raw, terms, redirect, exp);
    }

    private static SpfTerm ParseTerm(string token)
    {
        // Modifiers are name=value where the name cannot contain ':' — a ':' before any
        // '=' means mechanism-with-domain (include:..., ip4:...), not a modifier.
        var eq = token.IndexOf('=');
        var colon = token.IndexOf(':');
        if (eq > 0 && (colon < 0 || eq < colon))
        {
            return ParseModifier(token, eq);
        }

        return ParseMechanism(token);
    }

    private static SpfTerm ParseModifier(string token, int eq)
    {
        var name = token[..eq].Trim();
        var value = token[(eq + 1)..].Trim();

        if (name.Equals("redirect", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(value))
            {
                return Invalid(token, "redirect= with no domain.");
            }

            // redirect= costs a lookup like a mechanism (RFC 7208 §4.6.4).
            return new SpfTerm(token, SpfTermKind.Redirect, "+", value, null, null,
                HasMacro(value), CostsLookup: true, IsDynamic: HasMacro(value), ParseError: null);
        }

        if (name.Equals("exp", StringComparison.OrdinalIgnoreCase))
        {
            return new SpfTerm(token, SpfTermKind.Exp, "+", value, null, null,
                HasMacro(value), CostsLookup: false, IsDynamic: false, ParseError: null);
        }

        // RFC 7208 §6: unknown modifiers MUST be ignored.
        return new SpfTerm(token, SpfTermKind.UnknownModifier, "+", value, null, null,
            HasMacro(value), CostsLookup: false, IsDynamic: false, ParseError: null);
    }

    private static SpfTerm ParseMechanism(string token)
    {
        var qualifier = "+";
        var rest = token;
        if (rest.Length > 0 && "+-~?".Contains(rest[0]))
        {
            qualifier = rest[..1];
            rest = rest[1..];
        }

        // Split name from arguments at the first ':' or '/'.
        var argAt = rest.IndexOfAny([':', '/']);
        var name = (argAt < 0 ? rest : rest[..argAt]).ToLowerInvariant();
        var args = argAt < 0 ? string.Empty : rest[argAt..];

        return name switch
        {
            "all" => args.Length == 0
                ? new SpfTerm(token, SpfTermKind.All, qualifier, null, null, null, false, false, false, null)
                : Invalid(token, $"all takes no arguments (\"{args}\")."),
            "include" => ParseDomainMechanism(token, SpfTermKind.Include, qualifier, args, domainRequired: true),
            "exists" => ParseDomainMechanism(token, SpfTermKind.Exists, qualifier, args, domainRequired: true),
            "a" => ParseOptionalDomainMechanism(token, SpfTermKind.A, qualifier, args),
            "mx" => ParseOptionalDomainMechanism(token, SpfTermKind.Mx, qualifier, args),
            "ptr" => ParseOptionalDomainMechanism(token, SpfTermKind.Ptr, qualifier, args),
            "ip4" => ParseIpMechanism(token, SpfTermKind.Ip4, qualifier, args, maxCidr: 32),
            "ip6" => ParseIpMechanism(token, SpfTermKind.Ip6, qualifier, args, maxCidr: 128),
            _ => Invalid(token, $"Unknown mechanism \"{name}\"."),
        };
    }

    /// <summary>include:/exists: — the domain is mandatory, no CIDR allowed.</summary>
    private static SpfTerm ParseDomainMechanism(string token, string kind, string qualifier, string args, bool domainRequired)
    {
        if (!args.StartsWith(':'))
        {
            return domainRequired
                ? Invalid(token, $"{kind} requires a domain (\"{kind}:example.com\").")
                : Invalid(token, $"Malformed {kind} term.");
        }

        var target = args[1..];
        if (string.IsNullOrEmpty(target))
        {
            return Invalid(token, $"{kind} requires a domain (\"{kind}:example.com\").");
        }

        var macro = HasMacro(target);
        return new SpfTerm(token, kind, qualifier, target, null, null, macro,
            CostsLookup: true, IsDynamic: macro || kind == SpfTermKind.Exists, ParseError: null);
    }

    /// <summary>a/mx/ptr — optional :domain plus optional /cidr//cidr6.</summary>
    private static SpfTerm ParseOptionalDomainMechanism(string token, string kind, string qualifier, string args)
    {
        string? target = null;
        var rest = args;

        if (rest.StartsWith(':'))
        {
            var slash = rest.IndexOf('/');
            target = slash < 0 ? rest[1..] : rest[1..slash];
            rest = slash < 0 ? string.Empty : rest[slash..];
            if (string.IsNullOrEmpty(target))
            {
                return Invalid(token, $"{kind}: with no domain.");
            }
        }
        else if (rest.Length > 0 && !rest.StartsWith('/'))
        {
            return Invalid(token, $"Malformed {kind} term.");
        }

        var (cidr4, cidr6, cidrError) = ParseDualCidr(rest);
        if (cidrError is not null)
        {
            return Invalid(token, cidrError);
        }

        var macro = target is not null && HasMacro(target);
        return new SpfTerm(token, kind, qualifier, target, cidr4, cidr6, macro,
            CostsLookup: true, IsDynamic: macro, ParseError: null);
    }

    /// <summary>ip4:/ip6: — address mandatory, single /cidr optional, never a lookup.</summary>
    private static SpfTerm ParseIpMechanism(string token, string kind, string qualifier, string args, int maxCidr)
    {
        if (!args.StartsWith(':'))
        {
            return Invalid(token, $"{kind} requires an address (\"{kind}:198.51.100.0/24\").");
        }

        var body = args[1..];
        var slash = body.IndexOf('/');
        var address = slash < 0 ? body : body[..slash];
        if (!System.Net.IPAddress.TryParse(address, out _))
        {
            return Invalid(token, $"\"{address}\" is not an IP address.");
        }

        int? cidr = null;
        if (slash >= 0)
        {
            if (!int.TryParse(body[(slash + 1)..], out var parsed) || parsed < 0 || parsed > maxCidr)
            {
                return Invalid(token, $"\"{body}\" is not a /0-{maxCidr} prefix.");
            }

            cidr = parsed;
        }

        return new SpfTerm(token, kind, qualifier, address, cidr, null, false,
            CostsLookup: false, IsDynamic: false, ParseError: null);
    }

    private static (int? Cidr4, int? Cidr6, string? Error) ParseDualCidr(string rest)
    {
        if (rest.Length == 0)
        {
            return (null, null, null);
        }

        // Dual CIDR: /cidr or /cidr//cidr6. The second length opens with its own
        // slash, so split the body on "//" rather than on every '/'.
        var body = rest[1..];
        var halves = body.Split("//", StringSplitOptions.None);
        if (halves.Length > 2 || halves.Any(h => h.Contains('/')))
        {
            return (null, null, $"Malformed CIDR suffix \"{rest}\".");
        }

        if (!int.TryParse(halves[0], out var cidr4) || cidr4 is < 0 or > 32)
        {
            return (null, null, $"\"{rest}\" is not a /0-32 prefix.");
        }

        int? cidr6 = null;
        if (halves.Length == 2)
        {
            if (!int.TryParse(halves[1], out var parsed6) || parsed6 is < 0 or > 128)
            {
                return (null, null, $"\"{rest}\" is not a /0-32//0-128 prefix.");
            }

            cidr6 = parsed6;
        }

        return (cidr4, cidr6, null);
    }

    private static bool HasMacro(string value)
        => value.Contains("%{", StringComparison.Ordinal);

    private static SpfTerm Invalid(string token, string reason)
        => new(token, SpfTermKind.Invalid, "+", null, null, null, false, false, false, reason);
}
