using System.Security.Cryptography;
using System.Text.Json.Nodes;
using FvsBuild;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

public sealed class ReleasePublicationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fvs-release-tests-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("valid", true)]
    [InlineData("missing", false)]
    [InlineData("wrong digest", false)]
    [InlineData("duplicate", false)]
    public void EveryAssetMustMatchBeforeDraftBecomesPublic(string scenario, bool expected)
    {
        Directory.CreateDirectory(_root);
        string[] files = [Path.Combine(_root, "installer.exe"), Path.Combine(_root, "update.zip")];
        var assets = new JsonArray();
        foreach (string file in files)
        {
            File.WriteAllText(file, Path.GetFileName(file));
            assets.Add(new JsonObject { ["name"] = Path.GetFileName(file),
                ["digest"] = "sha256:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))) });
        }
        if (scenario == "missing") assets.RemoveAt(1);
        if (scenario == "wrong digest") assets[1]!["digest"] = "sha256:incorrect";
        if (scenario == "duplicate") assets.Add(assets[1]!.DeepClone());
        Assert.Equal(expected, GitHubReleasePublisher.VerifyAssets(files, new JsonObject { ["assets"] = assets }.ToJsonString()));
    }

    [Fact]
    public void NoDropper01_TheReleaseCarriesTheSetupAndTheOldNameBridgeButNoEmbeddedCompactInstaller()
    {
        string[] assets = GitHubReleasePublisher.ReleaseAssets(@"compiled\FreeVideoStudio-Setup.exe", "legacy.exe", "FreeVideoStudio.exe");
        Assert.Contains(@"compiled\FreeVideoStudio-Setup.exe", assets);
        Assert.Contains("FreeVideoStudio.exe", assets);
        Assert.Contains("legacy.exe", assets);
        Assert.DoesNotContain(assets, a => a.EndsWith("FreeVideoStudio.App.update.zip", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("FreeVideoStudio.exe", GitHubReleasePublisher.PreSetupDownloadName);
        Assert.Equal("FreeVideoStudio-Setup.exe", InnoInstaller.SetupFileName);
    }

    [Fact]
    public void MalformedReleaseResponseDoesNotPassVerification() =>
        Assert.False(GitHubReleasePublisher.VerifyAssets(["missing"], "{broken"));

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
