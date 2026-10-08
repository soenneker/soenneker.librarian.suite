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
    public async ValueTask Dispose_flushes_pending_changes(CancellationToken cancellationToken)
    {
        await using var fixture = new PersistenceFixture();
        ILibrarianContainer container = await fixture.Database.GetContainer("items", cancellationToken: cancellationToken);
        await container.AddItem("one", "payload", cancellationToken: cancellationToken);
        await fixture.Database.DisposeAsync();
        Check((await fixture.Files.Inner.Read(fixture.Path, cancellationToken: cancellationToken)).Contains("payload"), "Disposal lost the pending write.");
    }

    [Test]
    public async ValueTask Unload_saves_before_disposing_and_reloads_data(CancellationToken cancellationToken)
    {
        await using var fixture = new PersistenceFixture();
        ILibrarianContainer first = await fixture.Database.GetContainer("items", cancellationToken: cancellationToken);
        await first.AddItem("one", "payload", cancellationToken: cancellationToken);
        Check(await fixture.Database.UnloadContainer("items", cancellationToken: cancellationToken), "Container was not unloaded.");
        ILibrarianContainer second = await fixture.Database.GetContainer("items", cancellationToken: cancellationToken);
        Check(!ReferenceEquals(first, second) && (await second.GetItem("one", cancellationToken: cancellationToken)) == "payload", "Unload lost pending data.");
    }

    [Test]
    public async ValueTask Failed_atomic_write_preserves_original_and_retries(CancellationToken cancellationToken)
    {
        await using var fixture = new PersistenceFixture();
        ILibrarianContainer container = await fixture.Database.GetContainer("items", cancellationToken: cancellationToken);
        await container.AddItem("one", "payload", cancellationToken: cancellationToken);
        fixture.Files.BeforeWrite = async (stream, token) =>
        {
            await stream.WriteAsync(new byte[] { 1, 2, 3 }, token);
            throw new IOException("Injected write failure");
        };
        try { await fixture.Database.Save(cancellationToken: cancellationToken); throw new Exception("Expected save failure."); }
        catch (IOException) { }
        finally { fixture.Files.BeforeWrite = null; }
        Check((await fixture.Files.Inner.Read(fixture.Path, cancellationToken: cancellationToken)) == "{}", "Failed save damaged the original file.");
        await fixture.Database.Save(cancellationToken: cancellationToken);
        Check((await fixture.Files.Inner.Read(fixture.Path, cancellationToken: cancellationToken)).Contains("payload"), "Failed save lost dirty tracking.");
    }

    [Test]
    public async ValueTask Cancelled_write_remains_dirty(CancellationToken cancellationToken)
    {
        await using var fixture = new PersistenceFixture();
        ILibrarianContainer container = await fixture.Database.GetContainer("items", cancellationToken: cancellationToken);
        await container.AddItem("one", "payload", cancellationToken: cancellationToken);
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
        await fixture.Database.Save(cancellationToken: cancellationToken);
        Check((await fixture.Files.Inner.Read(fixture.Path, cancellationToken: cancellationToken)).Contains("payload"), "Cancellation lost dirty tracking.");
    }

    [Test]
    public async ValueTask Failed_load_remains_dirty_and_does_not_overwrite_corruption(CancellationToken cancellationToken)
    {
        await using var fixture = new PersistenceFixture();
        ILibrarianContainer container = await fixture.Database.GetContainer("items", cancellationToken: cancellationToken);
        await container.AddItem("one", "payload", cancellationToken: cancellationToken);
        await fixture.Files.Inner.Write(fixture.Path, "invalid JSON", cancellationToken: cancellationToken);
        try { await fixture.Database.Save(cancellationToken: cancellationToken); throw new Exception("Expected invalid JSON failure."); }
        catch (System.Text.Json.JsonException) { }
        finally
        {
            Check((await fixture.Files.Inner.Read(fixture.Path)) == "invalid JSON", "Corrupt input was overwritten.");
            await fixture.Files.Inner.Write(fixture.Path, "{}");
        }
        await fixture.Database.Save(cancellationToken: cancellationToken);
        Check((await fixture.Files.Inner.Read(fixture.Path, cancellationToken: cancellationToken)).Contains("payload"), "Load failure lost dirty tracking.");
    }

    [Test]
    public async ValueTask Save_preserves_unopened_containers_and_legacy_empty_files(CancellationToken cancellationToken)
    {
        await using var fixture = new PersistenceFixture("{\"unopened\":[{\"id\":\"old\",\"value\":\"kept\"}]}");
        ILibrarianContainer container = await fixture.Database.GetContainer("new", cancellationToken: cancellationToken);
        await container.AddItem("one", "payload", cancellationToken: cancellationToken);
        await fixture.Database.Save(cancellationToken: cancellationToken);
        Check((await (await fixture.Database.GetContainer("unopened", cancellationToken: cancellationToken)).GetItem("old", cancellationToken: cancellationToken)) == "kept", "Unopened data was lost.");
        await using var empty = new PersistenceFixture("");
        await (await empty.Database.GetContainer("items", cancellationToken: cancellationToken)).AddItem("one", "payload", cancellationToken: cancellationToken);
        await empty.Database.Save(cancellationToken: cancellationToken);
        Check((await fixture.Files.Inner.Read(empty.Path, cancellationToken: cancellationToken)).Contains("payload"), "Legacy empty database failed.");
    }

    [Test]
    public async ValueTask Mutation_during_save_is_saved_on_next_pass(CancellationToken cancellationToken)
    {
        await using var fixture = new PersistenceFixture();
        ILibrarianContainer container = await fixture.Database.GetContainer("items", cancellationToken: cancellationToken);
        await container.AddItem("one", "before", cancellationToken: cancellationToken);
        fixture.Files.BeforeWrite = async (_, _) => { await container.UpdateItem("one", "after", cancellationToken: cancellationToken); };
        await fixture.Database.Save(cancellationToken: cancellationToken);
        fixture.Files.BeforeWrite = null;
        await fixture.Database.Save(cancellationToken: cancellationToken);
        Check((await fixture.Files.Inner.Read(fixture.Path, cancellationToken: cancellationToken)).Contains("after"), "Concurrent mutation was lost.");
    }

    [Test]
    public async ValueTask Clean_save_and_identical_updates_do_not_write(CancellationToken cancellationToken)
    {
        await using var fixture = new PersistenceFixture();
        ILibrarianContainer container = await fixture.Database.GetContainer("items", cancellationToken: cancellationToken);
        await container.AddItem("one", "payload", cancellationToken: cancellationToken);
        await fixture.Database.Save(cancellationToken: cancellationToken);
        int writes = fixture.Files.Writes;
        await container.UpdateItem("one", "payload", cancellationToken: cancellationToken);
        await container.UpdateItemStrict("one", "payload", cancellationToken: cancellationToken);
        await fixture.Database.Save(cancellationToken: cancellationToken);
        Check(fixture.Files.Writes == writes, "Unchanged data triggered disk IO.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [Test]
    public async ValueTask Load_preserves_BOM_encoded_legacy_files(CancellationToken cancellationToken)
    {
        foreach (System.Text.Encoding encoding in new System.Text.Encoding[]
                 { new System.Text.UTF8Encoding(true), System.Text.Encoding.Unicode, System.Text.Encoding.BigEndianUnicode, System.Text.Encoding.UTF32 })
        {
            await using var fixture = new PersistenceFixture();
            // FileUtil writes UTF-8 without a BOM; this test requires explicit legacy encodings.
            File.WriteAllText(fixture.Path, "{\"items\":[{\"id\":\"one\",\"value\":\"payload\"}]}", encoding);
            ILibrarianContainer container = await fixture.Database.GetContainer("items", cancellationToken: cancellationToken);
            Check((await container.GetItem("one", cancellationToken: cancellationToken)) == "payload", "Legacy encoding was not preserved.");
        }
    }
}
