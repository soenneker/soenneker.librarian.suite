using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Maui.Storage;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class SecureStorageStub : ISecureStorage
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
    public bool Fail { get; set; }
    public Task<string?> GetAsync(string key) => Fail ? throw new InvalidOperationException("Secure storage unavailable") : Task.FromResult(Values.GetValueOrDefault(key));
    public Task SetAsync(string key, string value)
    {
        if (Fail) throw new InvalidOperationException("Secure storage unavailable");
        Values[key] = value;
        return Task.CompletedTask;
    }
    public bool Remove(string key) => Values.Remove(key);
    public void RemoveAll() => Values.Clear();
}
