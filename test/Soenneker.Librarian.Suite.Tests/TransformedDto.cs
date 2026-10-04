namespace Soenneker.Librarian.Suite.Tests;

public sealed class TransformedDto
{
    private decimal _value;
    public decimal Value { get => _value * 2; set => _value = value; }
}
