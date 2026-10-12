
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FreeVideoStudio.Core.Infrastructure;

/// <summary>
/// UPDATEPIN_01 — THE PUBLISHER ROOT, BUILT INTO THE APP.
///
/// <para>Releases are signed with the repository's own certificate (ssl-certificate\, SIGNLOCAL_01),
/// which chains to a PRIVATE root. No other PC trusts that root, so the old rule — "the running
/// build must chain to a root Windows trusts" — refused every in-app update on every user's PC
/// (UPDATETRUST_02) and sent them to download 296 MB by hand.</para>
///
/// <para>The fix needs no paid certificate: the root's PUBLIC certificate is compiled into the app.
/// An update is trusted when its Authenticode signature is intact AND its signer chains to THIS root
/// (custom trust, code-signing EKU). That is a stronger statement than "some public CA vouches for
/// someone": only the holder of this root's key can produce a file that passes. The same anchor
/// verifies the signed update manifest (<see cref="SmartUpdateManifest"/>).</para>
///
/// <para>⚠️ NEVER REGENERATE THE ROOT casually. A new root means every installed copy refuses every
/// future update until the user reinstalls by hand. ssl-certificate\create-dev-codesign-cert.ps1
/// makes a new root; FvsBuild refuses to sign with a certificate that does not chain to the root
/// below. A new LEAF from the same root (the leaf expires 2029-09-24) is fine.</para>
///
/// <para>Windows' own "unknown publisher" warning on the very FIRST download is not affected: that
/// needs a publicly trusted certificate, which only money buys.</para>
/// </summary>
public static class PublisherRoot
{
    /// <summary>SHA-256 fingerprint of the root below (CN=FVS Local Development Root CA, valid to 2036-09-22).</summary>
    public const string Sha256Thumbprint = "C2E12D96D138A6E11E96F8A84E6646961DE5D7F7CDC4DC841DD5F6229BF1946A";

    private const string CodeSigningOid = "1.3.6.1.5.5.7.3.3";

    private const string RootPem = """
-----BEGIN CERTIFICATE-----
MIIFZzCCA0+gAwIBAgIUDodCP8CcVZun5SmWHiFFAsNG9kEwDQYJKoZIhvcNAQEL
BQAwSjEmMCQGA1UEAwwdRlZTIExvY2FsIERldmVsb3BtZW50IFJvb3QgQ0ExEzAR
BgNVBAoMCkFsb24gUmVpY2gxCzAJBgNVBAYTAklMMB4XDTI2MDkyNTE4MTY0NloX
DTM2MDkyMjE4MTY0NlowSjEmMCQGA1UEAwwdRlZTIExvY2FsIERldmVsb3BtZW50
IFJvb3QgQ0ExEzARBgNVBAoMCkFsb24gUmVpY2gxCzAJBgNVBAYTAklMMIICIjAN
BgkqhkiG9w0BAQEFAAOCAg8AMIICCgKCAgEAwzJrwDRqlQsZJD+60EVu9MeXwgNP
yrVJIUpXMdwB6HFFYQXwfQWOymIvlK/oyIwRiQPjcRHwqMNKZEAUobIRvSoAkmCf
9cGxysNpvbk3ns2Q21vatz+Lh0HHi4/qMV/lvuoK/LUnlAzstGO9c0XmUlMlaLoJ
guxX9L/ihYDY7l8v226LnSKMLSBZ9w5VS7U/lIjN/6lls5Vic1aHV++mxCtNdrw3
2sVIWYEEWM/FsGMYE9sHpCEGLS58VJnXiYIpp2ttBztrKf3dphm2GEa46U8hlj3x
HhwTbUqtWVaNwSJAWq792nvo/CP9rxKL9HvC6c6s72ucbD9DHoJcyPhtteqTXQow
D63VCy/y7dWYIAIrLw6N26nOe0PCvy+LHyzHg7iM8Sxmj3PycglUdGyIDL0dnBts
PdJTwiXTqW84BThkrAD/0RUb3okSqEaJtaMiJ9HVVhAjK7c+MN9J0UTCDO70ZMUw
7N7pe9At0gouyL9vzdMLz5R8wHkfhlL5oIJ4MYsJo+jQOd/7PXv+0DJL6gDZXg8v
p3arUXd93e/sGxNm2qgmWuiRoPppc1p78hZHJ8IkRfN/+88AQWvEMUJRKhUc3vhX
oYAsLcWJtcGqjLyLhYtaJXgUJFvCfyG8B2aWIFa2c6a6+YIkTv3fL851w3Dm0Qy3
dpVk7tEuqWtFlvECAwEAAaNFMEMwEgYDVR0TAQH/BAgwBgEB/wIBADAOBgNVHQ8B
Af8EBAMCAQYwHQYDVR0OBBYEFPALMcbnVKrhjyTAdpsRru61bGeoMA0GCSqGSIb3
DQEBCwUAA4ICAQBe71J8QVNMoYaPufE2klFX7rQZNsBvNeUZzRE8KMeitsiI/2s7
/FgvmUoKRUw5RXkJDFdiLzVcv4qNB3TQ2+U
5l0DN/uff420y1oz3bxckTtn5rWFsvqnY9c2KyDN62H+ta1t+3aYoZtCydL4WO6d
dSTRzIQgVfgP8k/iXQ9y6MVjm0VEpdpQDteo7jQ7xUlvtYyHRwYQ16clIQg24/+F
pROHeBMrbaGoS/6WveM8q1OM4isxlvqc3SlMkYVymFEEjhe7TqZ4QOI3FwELtIgx
nziRiLcepoPExWqrbvZQytXoW66rMYiTcgsjb4LuGPSuExRU2+eRF3MAaPzXUWAR
hLUIK8fTGROxIa5iiQn0Z4diBJHM04Dk2RdH3339np4mgYhdu/iQj+lq0BZhi7nG
JqKyNg4TxoVrX4r+wR+kYMECS2oMU1fAwTYVCwRh+Jzin9qKmUzvopHBHNo14kA1
TS2XQUOlr8an4E9vEsCV8n742UTCRwcazk3AofhMQ/euGXeXwta41SRVQmaQhbHU
LzyeIFQ1gPtxdmMPNTQkip3x2YcULcwGbqx6bGHfvTwyDtUaptUw6a8ajCqVNMow
cjHIGEtVXiVCSOZClB2c5tGORCTdLkR1BZOA0HDWvXoPSRe96sCKRF7ESA==
-----END CERTIFICATE-----
""";

    /// <summary>The pinned root certificate (public part only).</summary>
    public static X509Certificate2 Load() => X509Certificate2.CreateFromPem(RootPem);

    /// <summary>
    /// True when <paramref name="signer"/> chains to the pinned root and may sign code. Time
    /// validity is not required: Authenticode signatures are RFC-3161 timestamped, so a file signed
    /// while the leaf was valid stays valid after it expires (SIGNMANDATE_01).
    /// </summary>
    public static bool Vouches(X509Certificate2 signer, out string detail)
    {
        using X509Certificate2 root = Load();
        if (!string.Equals(root.GetCertHashString(HashAlgorithmName.SHA256), Sha256Thumbprint, StringComparison.OrdinalIgnoreCase))
        {
            detail = "The built-in publisher root is damaged.";
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(CodeSigningOid));
        bool built = chain.Build(signer);
        X509ChainElement? top = chain.ChainElements.Count > 0 ? chain.ChainElements[^1] : null;
        bool anchored = top != null && string.Equals(
            top.Certificate.GetCertHashString(HashAlgorithmName.SHA256), Sha256Thumbprint, StringComparison.OrdinalIgnoreCase);
        if (built && anchored)
        {
            detail = "Signed by the Free Video Studio publisher key (pinned root).";
            return true;
        }

        detail = "The signer does not chain to the Free Video Studio publisher root"
               + (chain.ChainStatus.Length > 0 ? $" ({chain.ChainStatus[0].StatusInformation.Trim()})." : ".");
        return false;
    }
}
