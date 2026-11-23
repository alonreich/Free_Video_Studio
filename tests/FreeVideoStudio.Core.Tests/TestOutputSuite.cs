using System.Runtime.CompilerServices;

namespace FreeVideoStudio.Testing;

internal static class TestOutputSuite
{
    internal const string Name = "Core";

    [ModuleInitializer]
    internal static void Prepare()
    {
        string freezeCache = System.IO.Path.Combine(TestArtifactDirectory.SuiteWork(Name), "freeze-sfx");
        FreeVideoStudio.Core.Media.FreezeSoundEffect.CacheDirectoryProvider = () => freezeCache;
    }
}
