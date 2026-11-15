using Xunit;

// Several tests configure process-wide environment variables (GLITCHPORT_DSN)
// and build WebApplicationFactory hosts, so running test collections in parallel
// would make the process configuration racy. Requests inside a single test still
// run in parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
