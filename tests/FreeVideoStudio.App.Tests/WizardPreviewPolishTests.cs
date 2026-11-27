using FreeVideoStudio.App;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Testing;
using System.Text;
using Xunit;
namespace FreeVideoStudio.App.Tests;
public sealed class WizardPreviewPolishTests
{
    [Fact]
    public void AStaleFrameKeepsItsShapeWhileTheWindowIsResized()
    {
        var wide = MpvVideoView.AspectFit(1600, 900, 800, 900);
        Assert.Equal(800, wide.Width, 3);
        Assert.Equal(450, wide.Height, 3);
        Assert.Equal(225, wide.Y, 3);
        var same = MpvVideoView.AspectFit(1600, 900, 1600.4, 900.3);
        Assert.Equal(0, same.X);
        Assert.Equal(1600.4, same.Width, 3);
    }
    [Fact]
    public void TheCheckAndFinishTimelineShowsReadableTimes()
    {
        Assert.Equal(1, ReviewTimelineRuler.TickStep(12, 1300));
        Assert.Equal(5, ReviewTimelineRuler.TickStep(12, 300));
        Assert.Equal(30, ReviewTimelineRuler.TickStep(300, 700));
        Assert.Equal("0:08", ReviewTimelineRuler.Label(8));
        Assert.Equal("1:02:03", ReviewTimelineRuler.Label(3723));
    }
    [Fact]
    public async Task RepeatedPlayerErrorsEvictOldDataAndKeepRecordingAtTheByteLimit()
    {
        string path = NewLogPath();
        BoundedLogFile.Append(path, "OLDEST" + new string('a', 4999994));
        Assert.Equal(5000000, new FileInfo(path).Length);
        string error = "[error][libmpv_render] " + new string('界', 1300) + "\n";
        for (int i = 0; i < 3000; i++)
        {
            MpvLogCap.Enqueue(path, error);
            Assert.InRange(new FileInfo(path).Length, 0, 5000000);
        }
        MpvLogCap.Enqueue(path, "LAST PLAYER ERROR\n");
        string text = "";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!text.EndsWith("LAST PLAYER ERROR\n", StringComparison.Ordinal))
        {
            await Task.Delay(20, timeout.Token);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(file, new UTF8Encoding(false, true));
            if (file.Length >= 18)
            {
                file.Seek(-18, SeekOrigin.End);
                byte[] tail = new byte[18];
                int n = file.Read(tail);
                if (Encoding.ASCII.GetString(tail, 0, n) != "LAST PLAYER ERROR\n") continue;
            }
            file.Position = 0;
            text = await reader.ReadToEndAsync(timeout.Token);
        }
        Assert.DoesNotContain("OLDEST", text);
        Assert.Contains("[error][libmpv_render]", text);
        Assert.InRange(new FileInfo(path).Length, 1, 5000000);
    }
    [Fact]
    public void OversizedUtf8WritesAndConcurrentWritersKeepNewestDataInOneFile()
    {
        string path = NewLogPath();
        BoundedLogFile.Append(path, new string('界', 2000000) + "\n");
        Assert.InRange(new FileInfo(path).Length, 4999997, 5000000);
        Parallel.For(0, 32, i => BoundedLogFile.Append(path, $"record-{i:D2}\n"));
        BoundedLogFile.Prepend(path, "VERDICT\n");
        string text = File.ReadAllText(path, new UTF8Encoding(false, true));
        Assert.StartsWith("VERDICT\n", text);
        for (int i = 0; i < 32; i++) Assert.Contains($"record-{i:D2}\n", text);
        Assert.InRange(new FileInfo(path).Length, 1, 5000000);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }
    private static string NewLogPath() => Path.Combine(Directory.CreateDirectory(Path.Combine(
        TestArtifactDirectory.SuiteWork(TestOutputSuite.Name), "LogCap", Guid.NewGuid().ToString("N"))).FullName, "test.log");
}
