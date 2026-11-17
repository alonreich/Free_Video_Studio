using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FreeVideoStudio.App.Services;

[SupportedOSPlatform("windows")]
internal static unsafe partial class AuthenticodeVerifier
{
    internal enum TrustVerdict
    {
        Trusted,

        NoAnchor,

        Rejected
    }

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_SAFER_FLAG = 0x00000100;

    private const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
    private const int TRUST_E_BAD_DIGEST = unchecked((int)0x80096010);
    private const int TRUST_E_EXPLICIT_DISTRUST = unchecked((int)0x800B0111);
    private const int TRUST_E_SUBJECT_NOT_TRUSTED = unchecked((int)0x800B0004);
    private const int CERT_E_UNTRUSTEDROOT = unchecked((int)0x800B0109);
    private const int CERT_E_CHAINING = unchecked((int)0x800B010A);
    private const int CERT_E_EXPIRED = unchecked((int)0x800B0101);

    private static readonly Guid GenericVerifyV2 =
        new(0x00AAC56B, 0xCD44, 0x11D0, 0x8C, 0xC2, 0x00, 0xC0, 0x4F, 0xC2, 0x95, 0xEE);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public nint pcwszFilePath;
        public nint hFile;
        public nint pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public nint pPolicyCallbackData;
        public nint pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public nint pUnion;
        public uint dwStateAction;
        public nint hWVTStateData;
        public nint pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public nint pSignatureSettings;
    }

    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(nint hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

    internal readonly record struct SignatureInfo(
        bool ChainValid,
        bool HasSignature,
        string Subject,
        string Thumbprint,
        string Detail,
        bool IntegrityValid = false,
        byte[]? SignerCertificate = null);

    internal static SignatureInfo Inspect(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return new SignatureInfo(false, false, string.Empty, string.Empty, "File does not exist.");
        }

        int trustResult;
        try
        {
            trustResult = VerifyTrust(filePath);
        }
        catch (Exception ex)
        {
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(ex);
            return new SignatureInfo(false, false, string.Empty, string.Empty,
                $"Trust provider unavailable ({ex.GetType().Name}: {ex.Message}).");
        }

        bool hasSignature = trustResult != TRUST_E_NOSIGNATURE;
        bool chainValid = trustResult == 0;
        bool integrityValid = chainValid || trustResult is CERT_E_UNTRUSTEDROOT or CERT_E_CHAINING;
        byte[]? raw = null;

        string subject = string.Empty;
        string thumbprint = string.Empty;
        if (hasSignature)
        {
            try
            {
#pragma warning disable SYSLIB0057
                using X509Certificate signer = X509Certificate.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
                subject = signer.Subject ?? string.Empty;
                thumbprint = signer.GetCertHashString(HashAlgorithmName.SHA256) ?? string.Empty;
                raw = signer.GetRawCertData();
            }
            catch (Exception ex)
            {
                chainValid = false;
                global::FreeVideoStudio.App.RuntimeLog.Swallowed(ex);
                return new SignatureInfo(false, true, string.Empty, string.Empty,
                    $"Signature present but the signer certificate could not be read ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        return new SignatureInfo(chainValid, hasSignature, subject, thumbprint, DescribeTrustResult(trustResult), integrityValid, raw);
    }

    internal static bool IsSignedByPinnedPublisher(SignatureInfo info, out string detail)
    {
        if (!info.HasSignature || !info.IntegrityValid || info.SignerCertificate is null)
        {
            detail = info.Detail;
            return false;
        }
        try
        {
            using X509Certificate2 signer = X509CertificateLoader.LoadCertificate(info.SignerCertificate);
            return FreeVideoStudio.Core.Infrastructure.PublisherRoot.Vouches(signer, out detail);
        }
        catch (CryptographicException ex)
        {
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(ex);
            detail = "The signer certificate could not be read.";
            return false;
        }
    }

    internal static TrustVerdict EvaluateUpdateCandidate(string candidatePath, string? anchorPath, out string detail,
        string? listedSha256 = null)
    {
        SignatureInfo pinned = Inspect(candidatePath);
        if (IsSignedByPinnedPublisher(pinned, out string pinDetail))
        {
            detail = $"{pinDetail} (sha256 {Shorten(pinned.Thumbprint)}).";
            return TrustVerdict.Trusted;
        }
        if (pinned.HasSignature && IsSharedPublisher(pinned.Subject))
            return EvaluateSharedPublisher(candidatePath, pinned, listedSha256, out detail);
        if (pinned.HasSignature && !pinned.IntegrityValid)
        {
            detail = $"The downloaded file's signature did not validate: {pinned.Detail}";
            return TrustVerdict.Rejected;
        }

        SignatureInfo anchor = string.IsNullOrWhiteSpace(anchorPath)
            ? new SignatureInfo(false, false, string.Empty, string.Empty, "The running executable's path is unknown.")
            : Inspect(anchorPath!);

        if (!anchor.ChainValid || string.IsNullOrWhiteSpace(anchor.Subject))
        {
            detail = $"The running build is not signed ({anchor.Detail}), so the update cannot be checked against a publisher.";
            return TrustVerdict.NoAnchor;
        }

        SignatureInfo candidate = Inspect(candidatePath);

        if (!candidate.HasSignature)
        {
            detail = "The downloaded file carries no digital signature, but this build is signed. Refusing to run it.";
            return TrustVerdict.Rejected;
        }

        if (!candidate.ChainValid)
        {
            detail = $"The downloaded file's signature did not validate: {candidate.Detail}";
            return TrustVerdict.Rejected;
        }

        if (!string.Equals(candidate.Subject, anchor.Subject, StringComparison.OrdinalIgnoreCase))
        {
            detail = "The downloaded file is signed by a different publisher than this build. Refusing to run it.";
            return TrustVerdict.Rejected;
        }

        detail = $"Signature valid and publisher matches this build (sha256 {Shorten(candidate.Thumbprint)}).";
        return TrustVerdict.Trusted;
    }

    internal const string SharedPublisherName = "SignPath Foundation";

    internal static bool IsSharedPublisher(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return false;
        foreach (string part in subject.Split(','))
        {
            string p = part.Trim();
            if (p.StartsWith("CN=", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p[3..].Trim().Trim('"'), SharedPublisherName, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    internal static TrustVerdict EvaluateSharedPublisher(string candidatePath, SignatureInfo info, string? listedSha256, out string detail)
    {
        if (!info.ChainValid)
        {
            detail = $"The SignPath Foundation signature did not validate: {info.Detail}";
            return TrustVerdict.Rejected;
        }
        if (string.IsNullOrWhiteSpace(listedSha256))
        {
            detail = "The file is signed by SignPath Foundation, which signs many open-source programs, and no publisher-signed update list names it. Refusing to run it.";
            return TrustVerdict.Rejected;
        }
        string actual;
        try { actual = FreeVideoStudio.Core.Infrastructure.UpgradeFiles.Hash(candidatePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(ex);
            detail = "The downloaded file could not be read for its fingerprint check.";
            return TrustVerdict.Rejected;
        }
        if (!string.Equals(actual, listedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            detail = "The file is signed by SignPath Foundation but is not the file the publisher's signed update list names. Refusing to run it.";
            return TrustVerdict.Rejected;
        }
        detail = $"Signed by SignPath Foundation for Free Video Studio and named in the publisher-signed update list (sha256 {Shorten(info.Thumbprint)}).";
        return TrustVerdict.Trusted;
    }

    private static string Shorten(string thumbprint)
        => string.IsNullOrEmpty(thumbprint) ? "unknown" :
           thumbprint.Length <= 12 ? thumbprint : thumbprint[..12] + "…";

    private static string DescribeTrustResult(int hr) => hr switch
    {
        0 => "Signature is valid and the certificate chains to a trusted root.",
        TRUST_E_NOSIGNATURE => "The file is not signed.",
        TRUST_E_BAD_DIGEST => "The file has been modified since it was signed.",
        TRUST_E_EXPLICIT_DISTRUST => "The signature is explicitly distrusted on this machine.",
        TRUST_E_SUBJECT_NOT_TRUSTED => "The signer is not trusted on this machine.",
        CERT_E_UNTRUSTEDROOT => "The certificate does not chain to a trusted root.",
        CERT_E_CHAINING => "The certificate chain could not be built.",
        CERT_E_EXPIRED => "The signing certificate has expired.",
        _ => $"The trust provider returned 0x{hr:X8}."
    };

    private static int VerifyTrust(string filePath)
    {
        fixed (char* pPath = filePath)
        {
            WINTRUST_FILE_INFO fileInfo = default;
            fileInfo.cbStruct = (uint)sizeof(WINTRUST_FILE_INFO);
            fileInfo.pcwszFilePath = (nint)pPath;
            fileInfo.hFile = nint.Zero;
            fileInfo.pgKnownSubject = nint.Zero;

            WINTRUST_DATA data = default;
            data.cbStruct = (uint)sizeof(WINTRUST_DATA);
            data.dwUIChoice = WTD_UI_NONE;
            data.fdwRevocationChecks = WTD_REVOKE_NONE;
            data.dwUnionChoice = WTD_CHOICE_FILE;
            data.pUnion = (nint)(&fileInfo);
            data.dwStateAction = WTD_STATEACTION_VERIFY;
            data.dwProvFlags = WTD_SAFER_FLAG;

            Guid action = GenericVerifyV2;
            int result;
            try
            {
                result = WinVerifyTrust(nint.Zero, ref action, ref data);
            }
            finally
            {
                data.dwStateAction = WTD_STATEACTION_CLOSE;
                Guid closeAction = GenericVerifyV2;
                try { WinVerifyTrust(nint.Zero, ref closeAction, ref data); } catch (System.Exception swallowed)
                {
                    global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed);
                }
            }

            return result;
        }
    }
}
