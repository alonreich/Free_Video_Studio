using System;
using System.IO;
using System.Security.Cryptography;
using FreeVideoStudio.App.Services;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// SIGNPATH_01 (docs/05_SYSTEM_LIFECYCLE_STORAGE.md #SYS-SIGNING) — a file signed with SignPath
/// Foundation's shared open-source certificate is trusted only when Windows trusts the signature AND
/// the publisher-signed update list names its exact SHA-256.
/// </summary>
public sealed class SignPathTrustTests
{
    private static AuthenticodeVerifier.SignatureInfo Shared(bool chainValid = true) =>
        new(chainValid, true, "CN=SignPath Foundation, O=SignPath Foundation, L=Lewes, S=Delaware, C=US", "AB", "ok", IntegrityValid: chainValid);

    [Theory]
    [InlineData("CN=SignPath Foundation, O=SignPath Foundation, C=US", true)]
    [InlineData("O=Someone, CN=SignPath Foundation", true)]
    [InlineData("CN=\"SignPath Foundation\"", true)]
    [InlineData("CN=SignPath Foundation Evil, C=US", false)]
    [InlineData("CN=signpath foundation", false)]
    [InlineData("O=SignPath Foundation, CN=Other", false)]
    [InlineData("CN=FVS Publisher", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void SignPath01_OnlyTheExactCommonNameIsTheSharedPublisher(string? subject, bool expected) =>
        Assert.Equal(expected, AuthenticodeVerifier.IsSharedPublisher(subject));

    [Fact]
    public void SignPath01_ASharedSignatureNeedsTheListedHash()
    {
        string file = Path.Combine(Path.GetTempPath(), "fvs-signpath-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(file, [1, 2, 3, 4]);
        try
        {
            string sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

            Assert.Equal(AuthenticodeVerifier.TrustVerdict.Trusted,
                AuthenticodeVerifier.EvaluateSharedPublisher(file, Shared(), sha.ToLowerInvariant(), out _));
            Assert.Equal(AuthenticodeVerifier.TrustVerdict.Rejected,
                AuthenticodeVerifier.EvaluateSharedPublisher(file, Shared(), null, out string noList));
            Assert.Contains("no publisher-signed update list", noList);
            Assert.Equal(AuthenticodeVerifier.TrustVerdict.Rejected,
                AuthenticodeVerifier.EvaluateSharedPublisher(file, Shared(), new string('0', 64), out string other));
            Assert.Contains("not the file", other);
            Assert.Equal(AuthenticodeVerifier.TrustVerdict.Rejected,
                AuthenticodeVerifier.EvaluateSharedPublisher(file, Shared(chainValid: false), sha, out _));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void SignPath01_TheSharedCertificateNeverVouchesForTheUpdateList()
    {
        foreach (string relative in new[] { "src/FreeVideoStudio.App/Services/UpdatePreparer.cs", "src/FreeVideoStudio.App/Services/UpgradeInstallWorker.cs" })
        {
            string code = File.ReadAllText(Path.Combine(RepoRoot.Path, relative.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Contains("!AuthenticodeVerifier.IsSharedPublisher(own.Subject)", code);
        }
        string preparer = File.ReadAllText(Path.Combine(RepoRoot.Path, "src", "FreeVideoStudio.App", "Services", "UpdatePreparer.cs"));
        Assert.Contains("RequirePublisher(installer, exe.Sha256)", preparer);
        Assert.Contains("RequirePublisher(installer, listedSha256)", preparer);
    }
}
