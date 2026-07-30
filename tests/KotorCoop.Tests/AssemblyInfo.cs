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
