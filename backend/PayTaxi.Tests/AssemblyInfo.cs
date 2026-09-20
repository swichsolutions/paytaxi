using Xunit;

// The integration fixtures boot the API in-process and pass their settings as environment
// variables (see ApiFixture). Two hosts starting at the same time would read each other's
// variables, so test collections run one after another. Unit tests are fast enough not to care.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
