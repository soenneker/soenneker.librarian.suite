using System.IO;
using System;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.File.Registrars;
using Soenneker.Utils.MemoryStream.Abstract;
using Soenneker.Librarian.FileSystem;

namespace Soenneker.Librarian.Suite.Tests;

internal sealed class PersistenceFixture : IAsyncDisposable
{
    public readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"librarian-{Guid.NewGuid():N}.json");
    private readonly ServiceProvider _services = new ServiceCollection().AddLogging().AddFileUtilAsSingleton().BuildServiceProvider();
    public readonly FileProxy Files;
    public readonly FileSystemLibrarianDatabase Database;

    public PersistenceFixture(string initial = "{}")
    {
        using (var writer = new StreamWriter(_services.GetRequiredService<IFileUtil>().OpenWrite(Path)))
            writer.Write(initial);
        IFileUtil proxy = DispatchProxy.Create<IFileUtil, FileProxy>();
        Files = (FileProxy)proxy;
        Files.Inner = _services.GetRequiredService<IFileUtil>();
        Database = new FileSystemLibrarianDatabase(Path, proxy, _services.GetRequiredService<IMemoryStreamUtil>(), NullLogger.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        try { await Database.DisposeAsync(); }
        finally
        {
            try { await Files.Inner.Delete(Path); }
            finally { await _services.DisposeAsync(); }
        }
    }
}
