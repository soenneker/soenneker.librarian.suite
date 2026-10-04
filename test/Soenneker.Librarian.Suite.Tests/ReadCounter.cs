using System.Threading;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class ReadCounter
{
    private readonly AsyncLocal<int[]> _scope = new();
    public int Value
    {
        get => _scope.Value?[0] ?? 0;
        set
        {
            if (value == 0 || _scope.Value is null) _scope.Value = [value];
            else _scope.Value[0] = value;
        }
    }
}
