using Xunit;

// Log.Listener is process-wide static state shared by all McpServer instances; run tests sequentially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
