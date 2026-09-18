using System.Runtime.CompilerServices;

namespace Soenneker.Librarian.Core.Indexes;

// Short query chains stay on the stack; unusually large expressions use an owned array.
[InlineArray(8)]
internal struct InlineBuffer<T>
{
    private T _element0;
}
