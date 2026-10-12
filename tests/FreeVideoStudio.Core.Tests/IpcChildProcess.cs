using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Ipc;

namespace FreeVideoStudio.Core.Tests;

/// <summary>
/// IPCOWNER_01 — entry point of this test assembly, used ONLY to run genuinely separate client
/// processes for <see cref="IpcTests"/>: <c>dotnet FreeVideoStudio.Core.Tests.dll --ipc-child update
/// &lt;root&gt; &lt;key&gt; &lt;count&gt; &lt;budgetMs&gt;</c>. The test runner never calls Main, so tests are unaffected
/// (GenerateProgramFile=false replaces the Test SDK's empty generated Main).
/// Exit codes: 0 every commit returned true, 2 a commit returned false, 64 bad arguments.
/// </summary>
public static class IpcChildProcess
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--pcm-child")
        {
            using var stdout = Console.OpenStandardOutput();
            if (args[1] == "fail")
            {
                Console.Error.WriteLine("simulated ffmpeg failure");
                stdout.Write(new byte[] { 1, 0, 2, 0, 3 });
                stdout.Flush();
                return 3;
            }
            var block = new byte[16 * 1024];
            while (true)
            {
                stdout.Write(block);
                stdout.Flush();
            }
        }

        if (args.Length != 6 || args[0] != "--ipc-child" || args[1] != "update") return 64;

        var paths = new ApplicationPaths(args[2]);
        string key = args[3];
        int count = int.Parse(args[4]);
        var store = new StateTransferStore(paths) { DeliveryBudgetForTests = TimeSpan.FromMilliseconds(int.Parse(args[5])) };

        for (int i = 0; i < count; i++)
        {
            bool ok = await store.UpdatePropertiesAsync(new JsonObject { [key] = $"{key}_{i}" });
            if (!ok)
            {
                Console.Error.WriteLine($"commit {i} of {key} returned false");
                return 2;
            }
        }
        return 0;
    }
}
