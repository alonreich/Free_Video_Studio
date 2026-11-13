using System.Runtime.CompilerServices;

namespace FreeVideoStudio.Testing;

/// <summary>TESTOUT_01: this suite writes only to logs\App_Results\ (replaced once per run).</summary>
internal static class TestOutputSuite
{
    internal const string Name = "App";

    [ModuleInitializer]
    internal static void Prepare() => TestArtifactDirectory.SuiteWork(Name);
}
