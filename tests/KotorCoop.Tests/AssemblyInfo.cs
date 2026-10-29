using Xunit;

// These are not isolated unit tests. MemoryBridgeTests and ValueScannerTests both attach
// to the test host process itself, allocate unmanaged blocks in it, and scan or snapshot
// its entire address space. Running those classes concurrently means one test is
// allocating and freeing blocks while another is sweeping for them, and several threads
// are each copying ~100 MB of the same heap. That is not a realistic workload and it has
// crashed the test host with an execution engine error.
//
// Serialising the assembly costs a few seconds and makes the suite deterministic.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

// Because the assembly is serial, the real-socket loopback/integration classes (party pool,
// shared inventory, stores, disconnect/host loss...) are ~90% of its ten minutes. They carry
// [Trait("Category", "Slow")]; tools/test-fast.ps1 skips them for the everyday loop (about a
// minute). The four heaviest (party pool, shared inventory, stores, disconnect recovery:
// about 7 of the 12 minutes) also carry [Trait("Category", "LocalOnly")]: CI and the release
// workflow skip those, so run the full suite (no filter but Soak) on your machine before a tag.
// The lighter Slow classes still run in CI.
