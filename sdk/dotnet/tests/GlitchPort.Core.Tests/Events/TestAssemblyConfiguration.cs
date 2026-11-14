using Xunit;

// The options tests read and write process-wide environment variables, so the
// assembly runs every test sequentially instead of racing other test classes.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
