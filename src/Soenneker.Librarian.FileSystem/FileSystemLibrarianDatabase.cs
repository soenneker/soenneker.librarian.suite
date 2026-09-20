using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Soenneker.Asyncs.Locks;
using Soenneker.Atomics.ValueBools;
using Soenneker.Dictionaries.Singletons;
using Soenneker.Dtos.IdValuePair;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using Soenneker.Utils.AsyncInitializers;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.MemoryStream.Abstract;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Core;
using System;
using System.Linq;
using Soenneker.Librarian.Abstractions.Transactions;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Librarian.FileSystem;

public sealed class FileSystemLibrarianDatabase : ILibrarianDatabase
{
    private readonly LibrarianBatchExecutor _batches = new();
    private readonly Dictionary<string, LibrarianContainer> _loadedContainers = new(StringComparer.Ordinal);
    private readonly string _filePath;
    private readonly IFileUtil _fileUtil;
    private readonly ILogger _logger;
    private readonly SingletonDictionary<ILibrarianContainer> _containers;
    private readonly CancellationTokenSource _cts = new();
    private readonly AsyncInitializer _initializer;
    private readonly Task _periodicSave;
    private readonly AsyncLock _saveGate = new();
    private readonly AsyncLock _fileGate = new();
    private readonly AsyncLock _dirtyLock = new();
    private readonly HashSet<string> _dirtyContainers = new(StringComparer.Ordinal);
    private ValueAtomicBool _disposed = new(false);

    public FileSystemLibrarianDatabase(IConfiguration configuration, IFileUtil fileUtil,
        IMemoryStreamUtil memoryStreamUtil, ILogger<FileSystemLibrarianDatabase> logger) : this(
        (configuration["Librarian:FileSystem:FilePath"] ?? throw new InvalidOperationException("Missing configuration: Librarian:FileSystem:FilePath")), fileUtil, memoryStreamUtil, logger)
    {
    }

    public FileSystemLibrarianDatabase(string filePath, IFileUtil fileUtil, IMemoryStreamUtil memoryStreamUtil,
        ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _fileUtil = fileUtil;
        _logger = logger;
        _initializer = new AsyncInitializer(async token =>
        {
            if (!await _fileUtil.Exists(_filePath, token).NoSync())
            {
                _logger.LogWarning("Librarian database file ({filePath}) not found. Creating new database...",
                    _filePath);
                await _fileUtil.WriteAtomically(_filePath, "{}", log: false, token).NoSync();
            }
        });
        _containers = new SingletonDictionary<ILibrarianContainer>(LoadContainer);
        _periodicSave = RunPeriodicSave(_cts.Token);
    }

    private async ValueTask<ILibrarianContainer> LoadContainer(string id, CancellationToken cancellationToken)
    {
        await _initializer.Init(cancellationToken).NoSync();
        Dictionary<string, List<IdValuePair>> data = await Load(cancellationToken).NoSync();
        data.TryGetValue(id, out List<IdValuePair>? containerData);
        var container = new LibrarianContainer(id, this, _logger, containerData, _batches.Gate);
        _loadedContainers[id] = container;
        return container;
    }

    private async Task RunPeriodicSave(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).NoSync())
            {
                try
                {
                    await Save(cancellationToken).NoSync();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // SavePending restores dirty IDs so the next tick can retry.
                    _logger.LogError(ex, "Error saving Librarian database ({filePath})", _filePath);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public async ValueTask Save(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed.Value, this);
        cancellationToken.ThrowIfCancellationRequested();
        using (await _dirtyLock.Lock(cancellationToken).NoSync())
        {
            if (_dirtyContainers.Count == 0)
                return;
        }

        using (await _saveGate.Lock(cancellationToken).NoSync())
        {
            ObjectDisposedException.ThrowIf(_disposed.Value, this);
            await SavePending(cancellationToken).NoSync();
        }
    }

    // Called only while holding _saveGate, including during final disposal.
    private async ValueTask SavePending(CancellationToken cancellationToken)
    {
        List<string> dirtyContainers;
        using (await _dirtyLock.Lock(cancellationToken).NoSync())
        {
            if (_dirtyContainers.Count == 0)
                return;
            dirtyContainers = [.. _dirtyContainers];
            _dirtyContainers.Clear();
        }

        try
        {
            await _initializer.Init(cancellationToken).NoSync();
            Dictionary<string, List<IdValuePair>> data = await Load(cancellationToken).NoSync();
            foreach (string id in dirtyContainers)
            {
                ILibrarianContainer container = await _containers.Get(id, cancellationToken).NoSync();
                data[id] = await container.GetLibrarianItems(cancellationToken).NoSync();
            }

            // Serialize directly to a sibling file; a failed write leaves the original intact.
            using (await _fileGate.Lock(cancellationToken).NoSync())
            {
                await _fileUtil.WriteAtomically(_filePath,
                    (stream, token) => new ValueTask(JsonSerializer.SerializeAsync(stream, data, FileSystemJsonContext.Default.Database, token)),
                    log: false, cancellationToken).NoSync();
            }
        }
        catch
        {
            // Restoration must succeed even when the save token has been cancelled.
            using (await _dirtyLock.Lock(CancellationToken.None).NoSync())
            {
                foreach (string id in dirtyContainers)
                    _dirtyContainers.Add(id);
            }

            throw;
        }
    }

    private async ValueTask<Dictionary<string, List<IdValuePair>>> Load(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // OpenRead does not share delete access on Windows. Coordinate readers with atomic replacement.
        using Releaser fileLease = await _fileGate.Lock(cancellationToken).NoSync();
        await using FileStream stream = _fileUtil.OpenRead(_filePath, log: false);
        // Older versions created zero-byte database files.
        if (stream.Length == 0)
            return new Dictionary<string, List<IdValuePair>>(StringComparer.Ordinal);

        var prefix = new byte[4];
        int length = await stream.ReadAtLeastAsync(prefix, prefix.Length, throwOnEndOfStream: false, cancellationToken)
                                 .NoSync();
        stream.Position = 0;
        if (length >= 3 && prefix[0] == 0xef && prefix[1] == 0xbb && prefix[2] == 0xbf)
            stream.Position = 3;
        else if ((length >= 2 &&
                  ((prefix[0] == 0xff && prefix[1] == 0xfe) || (prefix[0] == 0xfe && prefix[1] == 0xff))) ||
                 (length == 4 && prefix[0] == 0 && prefix[1] == 0 && prefix[2] == 0xfe && prefix[3] == 0xff))
        {
            // Preserve legacy BOM-detected UTF-16/32 files; normal UTF-8 files stay on the streaming path.
            using var reader = new StreamReader(stream);
            string json = await reader.ReadToEndAsync(cancellationToken).NoSync();
            return JsonSerializer.Deserialize(json, FileSystemJsonContext.Default.Database) ??
                   throw new InvalidDataException($"Librarian database '{_filePath}' must contain a JSON object.");
        }

        return await JsonSerializer.DeserializeAsync<Dictionary<string, List<IdValuePair>>>(stream,
                   FileSystemJsonContext.Default.Database, cancellationToken).NoSync() ??
               throw new InvalidDataException($"Librarian database '{_filePath}' must contain a JSON object.");
    }

    public async ValueTask MarkDirty(string containerName, CancellationToken cancellationToken = default)
    {
        // Mutations have already committed in memory. Cancellation must not make them unsaveable.
        using (await _dirtyLock.Lock(CancellationToken.None).NoSync())
        {
            ObjectDisposedException.ThrowIf(_disposed.Value, this);
            _dirtyContainers.Add(containerName);
        }
    }

    public async ValueTask<ILibrarianContainer> GetContainer(string containerName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        using (await _saveGate.Lock(cancellationToken).NoSync())
        {
            ObjectDisposedException.ThrowIf(_disposed.Value, this);
            return await _containers.Get(containerName, cancellationToken).NoSync();
        }
    }

    public async ValueTask<bool> Execute(LibrarianBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        using (await _saveGate.Lock(cancellationToken).NoSync())
        {
            ObjectDisposedException.ThrowIf(_disposed.Value, this);
            foreach (string name in batch.Writes.Select(write => write.Container).Concat(batch.Conditions.Select(condition => condition.Container)).Distinct(StringComparer.Ordinal))
                await _containers.Get(name, cancellationToken).NoSync();
            return await _batches.Execute(batch, _loadedContainers, PersistBatch, cancellationToken).NoSync();
        }
    }

    private async ValueTask PersistBatch(IReadOnlyDictionary<string, List<IdValuePair>> snapshots, CancellationToken token)
    {
        Dictionary<string, List<IdValuePair>> data = await Load(token).NoSync();
        foreach (KeyValuePair<string, List<IdValuePair>> pair in snapshots) data[pair.Key] = pair.Value;
        using (await _fileGate.Lock(token).NoSync())
        {
            await _fileUtil.WriteAtomically(_filePath,
                async (stream, cancellationToken) =>
                {
                    await JsonSerializer.SerializeAsync(stream, data, FileSystemJsonContext.Default.Database, cancellationToken).NoSync();
                    await stream.FlushAsync(cancellationToken).NoSync();
                    if (stream is FileStream file) file.Flush(flushToDisk: true);
                },
                log: false, token).NoSync();
        }
    }

    public async ValueTask<bool> UnloadContainer(string containerName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed.Value, this);
        using (await _saveGate.Lock(cancellationToken).NoSync())
        {
            ObjectDisposedException.ThrowIf(_disposed.Value, this);
            await SavePending(cancellationToken).NoSync();
            bool removed = await _containers.Remove(containerName, cancellationToken).NoSync();
            if (removed) _loadedContainers.Remove(containerName);
            return removed;
        }
    }

    public async ValueTask DisposeAsync()
    {
        using (await _dirtyLock.Lock(CancellationToken.None).NoSync())
        {
            if (!_disposed.TrySetTrue())
                return;
        }

        try
        {
            await _cts.CancelAsync().NoSync();
            await _periodicSave.NoSync();
            using (await _saveGate.Lock(CancellationToken.None).NoSync())
                await SavePending(CancellationToken.None).NoSync();
        }
        finally
        {
            try
            {
                await _containers.DisposeAsync().NoSync();
            }
            finally
            {
                await _initializer.DisposeAsync().NoSync();
                _cts.Dispose();
            }
        }
    }
}
