using Xunit;

// PostgresFixture.CreateDatabaseAsync clears Npgsql's process-wide connection pools, and the repair tests
// capture Console output: both assume one test at a time, so test parallelization is off.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
