using Soenneker.Extensions.ValueTask;
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Core;
using Soenneker.Blazor.Utils.ModuleImport.Abstract;

namespace Soenneker.Librarian.Browser;

public abstract class BrowserSnapshotLibrarianDatabase : SnapshotLibrarianDatabase, IBrowserLibrarianDatabase
{
    private const string _modulePath = "./_content/Soenneker.Librarian.Browser/librarian.js";
    private readonly IModuleImportUtil _moduleImportUtil;
    private readonly string _backend;
    private readonly string _key;
    private string? _expected;

    protected BrowserSnapshotLibrarianDatabase(IModuleImportUtil moduleImportUtil, ILogger logger, string backend,
        string key) : base(logger)
    {
        ArgumentNullException.ThrowIfNull(moduleImportUtil);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _moduleImportUtil = moduleImportUtil;
        _backend = backend;
        _key = key;
    }

    protected override async ValueTask<string?> ReadSnapshot(CancellationToken cancellationToken)
    {
        IJSObjectReference module =
            await _moduleImportUtil.GetContentModuleReference(_modulePath, cancellationToken).NoSync();
        _expected = await module.InvokeAsync<string?>("read", cancellationToken, _backend, _key).NoSync();
        return _expected;
    }

    protected override async ValueTask WriteSnapshot(string json, CancellationToken cancellationToken)
    {
        IJSObjectReference module =
            await _moduleImportUtil.GetContentModuleReference(_modulePath, cancellationToken).NoSync();
        // Interop cancellation/disconnection after dispatch may leave an unknown commit outcome. Reopen before retrying.
        bool written = await module
                             .InvokeAsync<bool>("compareExchange", cancellationToken, _backend, _key, _expected, json)
                             .NoSync();
        if (!written)
            throw new LibrarianConcurrencyException(
                "Browser storage changed in another owner. Discard this database and reopen it before retrying.");
        _expected = json;
    }

    public async ValueTask DiscardAsync()
    {
        await DisposeWithoutSaving().NoSync();
        _expected = null;
        // The scoped module importer owns the shared reference used by all browser database instances.
    }
}