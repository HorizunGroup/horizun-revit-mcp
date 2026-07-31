// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// Why this suite does not run in parallel. Same reason as the Core suite, plus
// one this side has had all along: PipeClient's DirectoryOverride and Target
// are STATIC, because in the running server they are per-process
// session state. Two test classes touching them at once is two tests sharing one
// variable.
//
// DiscoveryResolveTests got away with it by being the only class that did. It is
// no longer the only class that does - the shared-data-root tests read the same
// resolution path - so the guarantee is written down here instead of resting on
// there happening to be one of them.
// -----------------------------------------------------------------------------
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
