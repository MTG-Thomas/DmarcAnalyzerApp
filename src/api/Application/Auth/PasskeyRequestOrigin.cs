namespace DmarcAnalyzer.Api.Application.Auth;

public static class PasskeyRequestOrigin
{
    public static bool IsAllowed(HttpRequest request, PasskeyOptions options)
    {
        if (request.Headers.Origin.Count != 1
            || !Uri.TryCreate(request.Headers.Origin.ToString(), UriKind.Absolute, out var supplied)
            || supplied.UserInfo.Length > 0
            || supplied.AbsolutePath != "/"
            || supplied.Query.Length > 0
            || supplied.Fragment.Length > 0)
        {
            return false;
        }

        var origin = supplied.GetLeftPart(UriPartial.Authority);
        return options.Origins.Any(x => string.Equals(x.TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase));
    }
}
