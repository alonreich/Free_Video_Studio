using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using FreeVideoStudio.Core.Infrastructure;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>
/// DIST-SMART — an update downloads only files whose CONTENT changed, never trusts a name, and never
/// trusts a list that is not signed by the pinned publisher root.
/// </summary>
public sealed class SmartUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fvs-smart-tests-" + Guid.NewGuid().ToString("N"));
    private string Folder(string name) { string path = Path.Combine(_root, name); Directory.CreateDirectory(path); return path; }
    private static void Put(string root, string name, string text)
    { string path = Path.Combine(root, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }

    /// <summary>A release whose files are given as path → content, plus its smart manifest.</summary>
    private SmartUpdateManifest Release(string folder, params (string Path, string Text)[] files)
    {
        foreach ((string path, string text) in files) Put(folder, path, text);
        InstallPayload.WriteManifest(folder);
        var manifest = new SmartUpdateManifest
        {
            Version = "2026.1.2.3",
            InstallManifest = File.ReadAllText(Path.Combine(folder, InstallPayload.ManifestName))
        };
        foreach (InstallFile file in InstallPayload.ReadManifest(folder).Files)
            manifest.Files.Add(new SmartFile
            {
                Path = file.Path, Length = file.Length, Sha256 = file.Sha256,
                Asset = SmartUpdateManifest.ContentAssetName(file.Sha256), AssetLength = file.Length
            });
        return SmartUpdateManifest.Parse(manifest.ToJsonBytes());
    }

    private static HashSet<string> HashesOf(string folder) =>
        UpgradeFiles.Files(folder).Select(f => UpgradeFiles.Hash(f).ToLowerInvariant()).ToHashSet();

    [Fact]
    public void SameNameDifferentContentIsDownloaded_SameContentIsNot()
    {
        string installed = Folder("installed");
        Put(installed, InstallPayload.ExecutableName, "old program");
        Put(installed, "backend/codec.dll", "codec v1");
        Put(installed, "frontend/mpv.dll", "mpv");

        SmartUpdateManifest manifest = Release(Folder("release"),
            (InstallPayload.ExecutableName, "new program"),
            ("backend/codec.dll", "codec v2"),
            ("frontend/mpv.dll", "mpv"),
            ("frontend/new.dll", "brand new"));

        SmartPlan plan = manifest.Plan(HashesOf(installed), UpgradeFiles.Hash(Path.Combine(installed, InstallPayload.ExecutableName)));

        string[] downloaded = plan.Download.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "FreeVideoStudio.exe", "backend/codec.dll", "frontend/new.dll" }, downloaded);
        Assert.Null(plan.Patch);
        Assert.Equal(3, plan.ChangedFiles);
    }

    [Fact]
    public void AMovedOrRenamedFileWithTheSameContentIsReusedNotDownloaded()
    {
        string installed = Folder("installed");
        Put(installed, InstallPayload.ExecutableName, "program");
        Put(installed, "old/place.dll", "shared bytes");
        SmartUpdateManifest manifest = Release(Folder("release"),
            (InstallPayload.ExecutableName, "program"), ("new/place.dll", "shared bytes"));
        Assert.Empty(manifest.Plan(HashesOf(installed), null).Download);
    }

    [Fact]
    public void ThePatchIsUsedOnlyForTheExactInstalledProgram()
    {
        SmartUpdateManifest manifest = Release(Folder("release"), (InstallPayload.ExecutableName, "new program"));
        string target = manifest.Files[0].Sha256;
        string from = new string('A', 64);
        manifest.Patches.Add(new SmartPatch { FromSha256 = from, ToSha256 = target, Asset = SmartUpdateManifest.PatchAssetName(from, target), AssetLength = 3 });

        Assert.NotNull(manifest.Plan(new HashSet<string>(), from).Patch);
        Assert.Empty(manifest.Plan(new HashSet<string>(), from).Download);
        SmartPlan other = manifest.Plan(new HashSet<string>(), new string('B', 64));
        Assert.Null(other.Patch);
        Assert.Single(other.Download);
    }

    [Fact]
    public void AListThatDisagreesWithItselfIsRefused()
    {
        SmartUpdateManifest manifest = Release(Folder("release"), (InstallPayload.ExecutableName, "program"));
        manifest.Files[0].Asset = SmartUpdateManifest.ContentAssetName(new string('C', 64));
        Assert.Throws<InvalidDataException>(() => SmartUpdateManifest.Parse(manifest.ToJsonBytes()));
    }

    [Fact]
    public void BuildingFromTheListUsesDownloadedFilesAndReusesOnlyIdenticalOnes()
    {
        string installed = Folder("installed");
        Put(installed, InstallPayload.ExecutableName, "old program");
        Put(installed, "backend/codec.dll", "codec v1");
        Put(installed, "frontend/mpv.dll", "mpv");
        SmartUpdateManifest manifest = Release(Folder("release"),
            (InstallPayload.ExecutableName, "new program"), ("backend/codec.dll", "codec v2"), ("frontend/mpv.dll", "mpv"));

        string downloaded = Folder("downloaded");
        foreach (SmartFile file in manifest.Plan(HashesOf(installed), null).Download)
            File.Copy(Path.Combine(_root, "release", file.Path.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(downloaded, file.Sha256.ToLowerInvariant()));

        string result = Path.Combine(_root, "result");
        InstallPayload.ExtractFromSmartUpdate(manifest, downloaded, result, [installed]);
        Assert.Equal("codec v2", File.ReadAllText(Path.Combine(result, "backend", "codec.dll")));
        Assert.Equal("new program", File.ReadAllText(Path.Combine(result, InstallPayload.ExecutableName)));
        Assert.Equal("mpv", File.ReadAllText(Path.Combine(result, "frontend", "mpv.dll")));
    }

    /// <summary>NODROPPER_01 — the Inno Setup installer's unpacked folder (real names) installs; a changed byte still fails.</summary>
    [Fact]
    public void NoDropper01_TheInstallerFolderWithRealFileNamesInstallsAndIsStillHashChecked()
    {
        string unpacked = Folder("setup-unpacked");
        SmartUpdateManifest manifest = Release(unpacked,
            (InstallPayload.ExecutableName, "program"), ("backend/codec.dll", "codec"), ("starter/meme/a.png", "png"));

        string result = Path.Combine(_root, "result");
        InstallPayload.ExtractFromSmartUpdate(manifest, unpacked, result, []);
        Assert.Equal("codec", File.ReadAllText(Path.Combine(result, "backend", "codec.dll")));
        Assert.Equal("png", File.ReadAllText(Path.Combine(result, "starter", "meme", "a.png")));

        File.WriteAllText(Path.Combine(unpacked, "backend", "codec.dll"), "tampered");
        Assert.Throws<IOException>(() =>
            InstallPayload.ExtractFromSmartUpdate(manifest, unpacked, Path.Combine(_root, "result2"), []));
    }

    [Fact]
    public void BuildingFailsWhenAChangedFileWasNotDownloaded()
    {
        string installed = Folder("installed");
        Put(installed, InstallPayload.ExecutableName, "program");
        Put(installed, "backend/codec.dll", "codec v1");
        SmartUpdateManifest manifest = Release(Folder("release"),
            (InstallPayload.ExecutableName, "program"), ("backend/codec.dll", "codec v2"));
        Assert.Throws<IOException>(() =>
            InstallPayload.ExtractFromSmartUpdate(manifest, Folder("empty"), Path.Combine(_root, "result"), [installed]));
    }

    [Fact]
    public void AListSignedByAnyoneButThePinnedPublisherIsRefused()
    {
        SmartUpdateManifest manifest = Release(Folder("release"), (InstallPayload.ExecutableName, "program"));
        byte[] json = manifest.ToJsonBytes();
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Somebody Else", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 stranger = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
        byte[] signature = SmartUpdateManifest.Sign(json, stranger);

        Assert.False(SmartUpdateManifest.VerifySignature(json, signature, alsoTrustThumbprint: null, out _));
        json[^3] ^= 1;
        Assert.False(SmartUpdateManifest.VerifySignature(json, signature, null, out _));
    }

    [Fact]
    public void ThePinnedRootIsIntact()
    {
        using X509Certificate2 root = PublisherRoot.Load();
        Assert.Equal(PublisherRoot.Sha256Thumbprint, root.GetCertHashString(HashAlgorithmName.SHA256), ignoreCase: true);
        Assert.Contains("FVS Local Development Root CA", root.Subject);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
