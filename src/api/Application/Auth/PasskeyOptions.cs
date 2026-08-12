namespace DmarcAnalyzer.Api.Application.Auth;

public sealed class PasskeyOptions
{
    public const string SectionName = "Auth:Passkeys";

    public bool Enabled { get; set; }
    public string RelyingPartyId { get; set; } = string.Empty;
    public string RelyingPartyName { get; set; } = "DMARC Analyzer";
    public string[] Origins { get; set; } = [];

    public bool IsValid(bool isDevelopment)
    {
        if (!Enabled)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(RelyingPartyId)
            || RelyingPartyId.Contains("://", StringComparison.Ordinal)
            || RelyingPartyId.Contains('/', StringComparison.Ordinal)
            || !Uri.CheckHostName(RelyingPartyId).Equals(UriHostNameType.Dns)
            || string.IsNullOrWhiteSpace(RelyingPartyName)
            || Origins.Length == 0)
        {
            return false;
        }

        foreach (var value in Origins)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var origin)
                || origin.UserInfo.Length > 0
                || origin.AbsolutePath != "/"
                || origin.Query.Length > 0
                || !string.IsNullOrEmpty(origin.Fragment)
                || !string.Equals(origin.Host, RelyingPartyId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (origin.Scheme != Uri.UriSchemeHttps
                && !(isDevelopment && origin.Scheme == Uri.UriSchemeHttp && origin.Host == "localhost"))
            {
                return false;
            }
        }

        return true;
    }
}
