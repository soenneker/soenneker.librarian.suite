if (args.Contains("--live")) { await CosmosLiveBenchmarks.Run(args.Contains("--soenneker")); return; }
await CosmosSerializationBenchmarks.Run();
await CosmosPointReadBenchmarks.Run();
