using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.JSInterop;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class BrowserStorageRuntime(Dictionary<string, string> storage) : IJSRuntime, IJSObjectReference
{
    public int Imports { get; private set; }
    public bool FailWrite { get; set; }
    public bool Disposed { get; private set; }
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        cancellationToken.ThrowIfCancellationRequested();
        object? result;
        if (identifier == "import") { Imports++; result = this; }
        else
        {
            string key = args![0] + ":" + args[1];
            if (identifier == "read") result = storage.GetValueOrDefault(key);
            else if (identifier == "compareExchange")
            {
                if (FailWrite) throw new JSException("Quota exceeded");
                bool matches = storage.GetValueOrDefault(key) == (string?)args[2];
                if (matches) storage[key] = (string)args[3]!;
                result = matches;
            }
            else throw new NotSupportedException(identifier);
        }
        return ValueTask.FromResult((TValue)result!);
    }
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}
