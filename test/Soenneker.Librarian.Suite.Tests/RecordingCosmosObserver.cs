using System;
using System.Collections.Generic;
using Soenneker.Librarian.Cosmos;

namespace Soenneker.Librarian.Suite.Tests;

internal sealed class RecordingCosmosObserver : ICosmosLibrarianObserver
{
    internal List<CosmosLibrarianResponse> Responses { get; } = [];
    internal bool Throw { get; set; }
    public void OnResponse(CosmosLibrarianResponse response)
    {
        Responses.Add(response);
        if (Throw) throw new InvalidOperationException("Observer failure");
    }
}
