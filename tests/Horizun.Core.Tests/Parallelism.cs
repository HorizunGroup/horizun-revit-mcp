// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Why this suite does not run in parallel.
//
// HorizunPaths resolves the data root from the ENVIRONMENT, on every call, and
// the property that has to be proved is what happens when that environment
// differs. Proving it means setting %LOCALAPPDATA% and %USERPROFILE% - and
// environment variables are process-global, not per-test.
//
// xunit runs test CLASSES in parallel by default. A test that moves the root
// while JobRecordTests is deciding where to write its record is a flake that
// appears on someone else's machine, months later, and reads like a bug in the
// code under test.
//
// The whole suite is milliseconds. Serialising it costs nothing worth measuring
// and removes a class of failure that would be very expensive to diagnose.
// -----------------------------------------------------------------------------
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
