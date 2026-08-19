using System.Runtime.CompilerServices;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests;

/// <summary>
/// Disables the adaptive request pacer for the whole test assembly before any test runs.
/// Pacing is ON by default in production; without this, every pre-pacing test would inherit
/// slow-start spacing (a cold bucket caps at 4 rps) and pagination/fan-out tests would
/// multiply suite time. Pacer tests re-enable the gate locally via
/// AdaptiveRequestPacerTests.PacerScope, serialized through the Pipeline collection.
/// </summary>
internal static class PacerTestDefaults
{
    [ModuleInitializer]
    internal static void DisablePacingForSuite() =>
        AdaptiveRequestPacer.DisabledForTests = true;
}
