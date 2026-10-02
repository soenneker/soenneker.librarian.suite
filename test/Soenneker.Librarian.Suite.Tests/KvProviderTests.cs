using System;
using Soenneker.Cloudflare.Workers.Kv;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Kv;
using Soenneker.Librarian.Kv.Registrars;

namespace Soenneker.Librarian.Suite.Tests;

public class KvProviderTests
{
    [Test]
    public async ValueTask Oversized_snapshot_is_rejected_without_losing_pending_changes()
    {
        using var fixture = new CloudflareFixture("kv");
        await using ILibrarianDatabase db = fixture.Create();
        ILibrarianContainer items = await db.GetContainer("items");
        await items.AddItem("large", new string('x', 25 * 1024 * 1024));
        bool rejected = false;
        try { await db.Save(); }
        catch (InvalidOperationException) { rejected = true; }
        if (!rejected || fixture.Handler.Writes != 0)
            throw new Exception("Oversized snapshot reached Cloudflare.");
        if (await items.GetItem("large") is null)
            throw new Exception("Failed save lost pending data.");
        await items.DeleteItem("large");
        await db.Save();
        if (fixture.Handler.Writes != 1) throw new Exception("Save did not recover.");
    }

    [Test]
    [Arguments(".")]
    [Arguments("..")]
    [Arguments("")]
    public void Invalid_keys_are_rejected(string key)
    {
        using var fixture = new CloudflareFixture("kv");
        try
        {
            _ = new KvLibrarianDatabase("account", "token", "namespace", new CloudflareWorkersKvUtil(fixture.ClientUtil, NullLogger<CloudflareWorkersKvUtil>.Instance), NullLogger.Instance, key);
        }
        catch (ArgumentException) { return; }
        throw new Exception("Invalid key was accepted.");
    }

    [Test]
    public void Key_limit_uses_utf8_bytes()
    {
        using var fixture = new CloudflareFixture("kv");
        try
        {
            _ = new KvLibrarianDatabase("account", "token", "namespace", new CloudflareWorkersKvUtil(fixture.ClientUtil, NullLogger<CloudflareWorkersKvUtil>.Instance), NullLogger.Instance, new string('\u00e9', 257));
        }
        catch (ArgumentException) { return; }
        throw new Exception("Oversized UTF-8 key was accepted.");
    }

    [Test]
    public async ValueTask Keyed_factory_registration_is_a_singleton_and_preserves_existing_registration()
    {
        using var fixture = new CloudflareFixture("kv");
        var services = new ServiceCollection();
        services.AddKvLibrarianDatabaseAsSingleton("kv", _ => (KvLibrarianDatabase)fixture.Create());
        services.AddKvLibrarianDatabaseAsSingleton("kv", _ => throw new Exception("Registration was replaced."));
        await using ServiceProvider provider = services.BuildServiceProvider();
        ILibrarianDatabase db = provider.GetRequiredKeyedService<ILibrarianDatabase>("kv");
        if (!ReferenceEquals(db, provider.GetRequiredKeyedService<ILibrarianDatabase>("kv")))
            throw new Exception("Keyed registration is not a singleton.");
        await (await db.GetContainer("items")).AddItem("a", "keyed");
        await db.Save();
        if (fixture.Handler.Snapshot?.Contains("keyed", StringComparison.Ordinal) != true)
            throw new Exception("Keyed provider did not persist.");
    }
}
