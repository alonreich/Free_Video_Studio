using System.Runtime.CompilerServices;

namespace FreeVideoStudio.Testing;

internal static class TestOutputSuite
{
    internal const string Name = "Verifier";

    [ModuleInitializer]
    internal static void Prepare() => TestArtifactDirectory.SuiteWork(Name);
}
