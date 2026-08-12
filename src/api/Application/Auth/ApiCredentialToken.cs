using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace DmarcAnalyzer.Api.Application.Auth;

internal static class ApiCredentialToken
{
    private static readonly byte[] MissingCredentialHash = new byte[32];

    public static bool TryGetPrefix(string? token, string scheme, out string prefix)
    {
        prefix = string.Empty;
        if (token is null)
        {
            return false;
        }

        var parts = token.Split('.');
        if (parts.Length != 3
            || !string.Equals(parts[0], scheme, StringComparison.Ordinal)
            || parts[1].Length != 22
            || parts[2].Length != 43
            || !Base64Url.IsValid(parts[1])
            || !Base64Url.IsValid(parts[2]))
        {
            return false;
        }

        prefix = parts[1];
        return true;
    }

    public static bool HashMatches(string token, byte[]? expectedHash)
    {
        var candidateHash = SHA256.HashData(Encoding.ASCII.GetBytes(token));
        return CryptographicOperations.FixedTimeEquals(
            candidateHash,
            expectedHash ?? MissingCredentialHash);
    }
}
