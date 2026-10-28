using System.Collections.Generic;
using Microsoft.Azure.Cosmos;

namespace Soenneker.Librarian.Cosmos;

internal static class CosmosQueryOptions
{
    // Request options are mutable; page-size and partition overrides must never modify caller-owned options.
    internal static QueryRequestOptions Copy(QueryRequestOptions source) => new()
    {
        PartitionKey = source.PartitionKey,
        MaxItemCount = source.MaxItemCount,
        MaxConcurrency = source.MaxConcurrency,
        MaxBufferedItemCount = source.MaxBufferedItemCount,
        ReadConsistencyStrategy = source.ReadConsistencyStrategy,
        ConsistencyLevel = source.ConsistencyLevel,
        SessionToken = source.SessionToken,
        EnableOptimisticDirectExecution = source.EnableOptimisticDirectExecution,
        EnableScanInQuery = source.EnableScanInQuery,
        EnableLowPrecisionOrderBy = source.EnableLowPrecisionOrderBy,
        PopulateIndexMetrics = source.PopulateIndexMetrics,
        PopulateQueryAdvice = source.PopulateQueryAdvice,
        ResponseContinuationTokenLimitInKb = source.ResponseContinuationTokenLimitInKb,
        DedicatedGatewayRequestOptions = source.DedicatedGatewayRequestOptions,
        QueryTextMode = source.QueryTextMode,
        FullTextScoreScope = source.FullTextScoreScope,
        IfMatchEtag = source.IfMatchEtag,
        IfNoneMatchEtag = source.IfNoneMatchEtag,
        Properties = source.Properties is null ? null : new Dictionary<string, object>(source.Properties),
        AddRequestHeaders = source.AddRequestHeaders,
        PriorityLevel = source.PriorityLevel,
        CosmosThresholdOptions = source.CosmosThresholdOptions,
        ExcludeRegions = source.ExcludeRegions is null ? null : new List<string>(source.ExcludeRegions),
        AvailabilityStrategy = source.AvailabilityStrategy,
        ThroughputBucket = source.ThroughputBucket
    };
}
