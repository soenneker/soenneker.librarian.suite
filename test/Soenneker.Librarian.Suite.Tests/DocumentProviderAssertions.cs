using System;

namespace Soenneker.Librarian.Suite.Tests;

internal static class DocumentProviderAssertions
{
    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
