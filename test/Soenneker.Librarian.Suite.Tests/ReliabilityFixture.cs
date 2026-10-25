using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soenneker.Librarian.FileSystem;
using Soenneker.Librarian.Redis;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.File.Registrars;
using Soenneker.Utils.MemoryStream.Abstract;

namespace Soenneker.Librarian.Suite.Tests;

internal sealed class ReliabilityFixture : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly RedisPersistenceFixture? _redis;
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"librarian-reliability-{Guid.NewGuid()}.json");

    public ReliabilityFixture(bool redis = false)
    {
        var services = new ServiceCollection().AddLogging().AddFileUtilAsSingleton();
        if (redis)
        {
            _redis = new RedisPersistenceFixture();
            services.AddSingleton(_redis.Database);
        }
        services.AddSingleton(sp => new FileSystemLibrarianDatabase(Path, sp.GetRequiredService<IFileUtil>(),
            sp.GetRequiredService<IMemoryStreamUtil>(), sp.GetRequiredService<ILogger<FileSystemLibrarianDatabase>>()));
        _provider = services.BuildServiceProvider();
    }

    public T Resolve<T>() where T : notnull => _provider.GetRequiredService<T>();

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        if (_redis is not null) await _redis.DisposeAsync();
        if (File.Exists(Path)) File.Delete(Path);
    }
}
