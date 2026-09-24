using Soenneker.Utils.File.Abstract;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Librarian.Suite.Tests;

public class PersistenceTests
{

    [Test]
    public async Task Dispose_flushes_pending_changes()
    {
        await using var fixture = new PersistenceFixture();
        ILibrarianContainer container = await fixture.Database.GetContainer("items");
        await container.AddItem("one", "payload");
        await fixture.Database.DisposeAsync();
        Check((await fixture.Files.Inner.Read(fixture.Path)).Contains("payload"), "Disposal lost the pending write.");
    }

    [Test]
    public async Task Unload_saves_before_disposing_and_reloads_data()
    {
        await using var fixture = new PersistenceFixture();
        ILibrarianContainer first = await fixture.Database.GetContainer("items");
        await first.AddItem("one", "payload");
        Check(await fixture.Database.UnloadContainer("items"), "Container was not unloaded.");
        ILibrarianContainer second = await fixture.Database.GetContainer("items");
        Check(!ReferenceEquals(first, second) && (await second.GetItem("one")) == "payload", "Unload lost pending data.");
    }

    [Test]
    public async Task Failed_atomic_write_preserves_original_and_retries()
    {
        await using var fixture = new PersistenceFixture();
        ILibrarianContainer container = await fixture.Database.GetContainer("items");
        await container.AddItem("one", "payload");
        fixture.Files.BeforeWrite = async (stream, token) =>
        {
            await stream.WriteAsync(new byte[] { 1, 2, 3 }, token);
            throw new IOException("Injected write failure");
        };
        try { await fixture.Database.Save(); throw new Exception("Expected save failure."); }
        catch (IOException) { }
        finally { fixture.Files.BeforeWrite = null; }
        Check((await fixture.Files.Inner.Read(fixture.Path)) == "{}", "Failed save damaged the original file.");
        await fixture.Database.Save();
        Check((await fixture.Files.Inner.Read(fixture.Path)).Contains("payload"), "Failed save lost dirty tracking.");
    }

    [Test]
    public async Task Cancelled_write_remains_dirty()
    {
        await using var fixture = new PersistenceFixture();
        ILibrarianContainer container = await fixture.Database.GetContainer("items");
        await container.AddItem("one", "payload");
        using var cts = new CancellationTokenSource();
        fixture.Files.BeforeWrite = (_, token) =>
        {
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        };
        try { await fixture.Database.Save(cts.Token); throw new Exception("Expected cancellation."); }
        catch (OperationCanceledException) { }
        finally { fixture.Files.BeforeWrite = null; }
        await fixture.Database.Save();
        Check((await fixture.Files.Inner.Read(fixture.Path)).Contains("payload"), "Cancellation lost dirty tracking.");
    }

    [Test]
    public async Task Failed_load_remains_dirty_and_does_not_overwrite_corruption()
    {
        await using var fixture = new PersistenceFixture();
        ILibrarianContainer container = await fixture.Database.GetContainer("items");
        await container.AddItem("one", "payload");
        await fixture.Files.Inner.Write(fixture.Path, "invalid JSON");
        try { await fixture.Database.Save(); throw new Exception("Expected invalid JSON failure."); }
        catch (System.Text.Json.JsonException) { }
        finally
        {
            Check((await fixture.Files.Inner.Read(fixture.Path)) == "invalid JSON", "Corrupt input was overwritten.");
            await fixture.Files.Inner.Write(fixture.Path, "{}");
        }
        await fixture.Database.Save();
        Check((await fixture.Files.Inner.Read(fixture.Path)).Contains("payload"), "Load failure lost dirty tracking.");
    }

    [Test]
    public async Task Save_preserves_unopened_containers_and_legacy_empty_files()
    {
        await using var fixture = new PersistenceFixture("{\"unopened\":[{\"id\":\"old\",\"value\":\"kept\"}]}");
        ILibrarianContainer container = await fixture.Database.GetContainer("new");
        await container.AddItem("one", "payload");
        await fixture.Database.Save();
        Check((await (await fixture.Database.GetContainer("unopened")).GetItem("old")) == "kept", "Unopened data was lost.");
        await using var empty = new PersistenceFixture("");
        await (await empty.Database.GetContainer("items")).AddItem("one", "payload");
        await empty.Database.Save();
        Check((await fixture.Files.Inner.Read(empty.Path)).Contains("payload"), "Legacy empty database failed.");
    }

    [Test]
    public async Task Mutation_during_save_is_saved_on_next_pass()
    {
        await using var fixture = new PersistenceFixture();
        ILibrarianContainer container = await fixture.Database.GetContainer("items");
        await container.AddItem("one", "before");
        fixture.Files.BeforeWrite = async (_, _) => { await container.UpdateItem("one", "after"); };
        await fixture.Database.Save();
        fixture.Files.BeforeWrite = null;
        await fixture.Database.Save();
        Check((await fixture.Files.Inner.Read(fixture.Path)).Contains("after"), "Concurrent mutation was lost.");
    }

    [Test]
    public async Task Clean_save_and_identical_updates_do_not_write()
    {
        await using var fixture = new PersistenceFixture();
        ILibrarianContainer container = await fixture.Database.GetContainer("items");
        await container.AddItem("one", "payload");
        await fixture.Database.Save();
        int writes = fixture.Files.Writes;
        await container.UpdateItem("one", "payload");
        await container.UpdateItemStrict("one", "payload");
        await fixture.Database.Save();
        Check(fixture.Files.Writes == writes, "Unchanged data triggered disk IO.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [Test]
    public async Task Load_preserves_BOM_encoded_legacy_files()
    {
        foreach (System.Text.Encoding encoding in new System.Text.Encoding[]
                 { new System.Text.UTF8Encoding(true), System.Text.Encoding.Unicode, System.Text.Encoding.BigEndianUnicode, System.Text.Encoding.UTF32 })
        {
            await using var fixture = new PersistenceFixture();
            // FileUtil writes UTF-8 without a BOM; this test requires explicit legacy encodings.
            System.IO.File.WriteAllText(fixture.Path, "{\"items\":[{\"id\":\"one\",\"value\":\"payload\"}]}", encoding);
            ILibrarianContainer container = await fixture.Database.GetContainer("items");
            Check((await container.GetItem("one")) == "payload", "Legacy encoding was not preserved.");
        }
    }
}
