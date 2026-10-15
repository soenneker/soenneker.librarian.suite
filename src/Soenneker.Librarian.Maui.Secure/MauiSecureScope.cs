using System;
using System.Security.Cryptography;
using System.Text;

namespace Soenneker.Librarian.Maui.Secure;

internal static class MauiSecureScope
{
    private static readonly UTF8Encoding Encoding = new(false, true);

    internal static string GetHash(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return Convert.ToHexString(SHA256.HashData(Encoding.GetBytes(scope)));
    }
}
