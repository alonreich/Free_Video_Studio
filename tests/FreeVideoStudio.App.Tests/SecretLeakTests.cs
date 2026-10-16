using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// SECRETSCAN_01 (docs/05 SYS-AISETTINGS) — nothing that would let a stranger use the developer's
/// accounts may be committed: Google API keys, private keys, GitHub tokens, or the code-signing
/// certificate's private half and password. CI runs this on every push, so a pasted key fails the
/// build BEFORE it reaches a public release.
///
/// <para>Only files git tracks (or would add) are scanned — the developer's own git-ignored
/// <c>ssl-certificate/*.pfx</c>, <c>*.key.pem</c> and <c>*.password.txt</c> stay on their machine and
/// are deliberately not read. A line can opt out with the marker <c>SECRETSCAN_ALLOW</c> (fake test keys).</para>
/// </summary>
public sealed class SecretLeakTests
{
    private static readonly (string Name, Regex Pattern)[] Patterns =
    {
        ("Google API key", new Regex(@"AIza[0-9A-Za-z_\-]{35}(?![0-9A-Za-z_\-])", RegexOptions.CultureInvariant)),
        ("private key block", new Regex(@"-----BEGIN (RSA |EC |DSA |OPENSSH |ENCRYPTED )?PRIVATE KEY-----", RegexOptions.CultureInvariant)),
        ("GitHub token", new Regex(@"\b(ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{36}\b|\bgithub_pat_[A-Za-z0-9_]{40,}", RegexOptions.CultureInvariant)),
        ("Anthropic/OpenAI style key", new Regex(@"\bsk-(ant-)?[A-Za-z0-9_\-]{32,}", RegexOptions.CultureInvariant)),
    };

    private static readonly string[] ForbiddenFileGlobs = { ".pfx", ".p12", ".key.pem", ".password.txt", ".snk" };

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".ico", ".mp4", ".mp3", ".wav", ".webm", ".mkv", ".dll", ".exe", ".zip",
        ".7z", ".pdf", ".ttf", ".otf", ".woff", ".woff2", ".bin", ".dat", ".so", ".pdb", ".cer",
    };

    /// <summary>Files git tracks plus untracked files it would add (respects .gitignore). Null without git.</summary>
    private static IReadOnlyList<string>? GitCandidateFiles()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "ls-files -z --cached --others --exclude-standard")
            {
                WorkingDirectory = RepoRoot.Path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(30_000);
            if (p.ExitCode != 0) return null;
            return output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(rel => Path.Combine(RepoRoot.Path, rel.Replace('/', Path.DirectorySeparatorChar)))
                .Where(File.Exists)
                .ToList();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>Fallback without git: the source, test, docs and build trees (never ssl-certificate/).</summary>
    private static IReadOnlyList<string> FallbackFiles()
    {
        var roots = new[] { "src", "tests", "docs", "build", ".github" }.Select(r => Path.Combine(RepoRoot.Path, r)).Where(Directory.Exists);
        string sep = Path.DirectorySeparatorChar.ToString();
        return roots.SelectMany(r => Directory.EnumerateFiles(r, "*", SearchOption.AllDirectories))
            .Where(f => !f.Contains(sep + "bin" + sep) && !f.Contains(sep + "obj" + sep) && !f.Contains(sep + "TestResults" + sep))
            .ToList();
    }

    private static IReadOnlyList<string> CandidateFiles() => GitCandidateFiles() ?? FallbackFiles();

    [Fact]
    public void NoSecretsInFilesThatCouldBeCommitted()
    {
        var offenders = new List<string>();
        foreach (string file in CandidateFiles())
        {
            if (BinaryExtensions.Contains(Path.GetExtension(file))) continue;
            var info = new FileInfo(file);
            if (info.Length > 5_000_000) continue;

            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch (IOException) { continue; }

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Contains("SECRETSCAN_ALLOW", StringComparison.Ordinal)) continue;
                foreach (var (name, pattern) in Patterns)
                {
                    if (pattern.IsMatch(line))
                        offenders.Add($"{Path.GetRelativePath(RepoRoot.Path, file)}:{i + 1}  ({name})");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "SECRETSCAN_01 — these lines look like real credentials. Remove them (and rotate the key: "
          + "anything that reached a commit must be treated as leaked). Fake test values: mark the line "
          + "with SECRETSCAN_ALLOW." + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void NoKeyOrCertificateFilesCanBeCommitted()
    {
        var offenders = CandidateFiles()
            .Where(f => ForbiddenFileGlobs.Any(g => f.EndsWith(g, StringComparison.OrdinalIgnoreCase)))
            .Select(f => Path.GetRelativePath(RepoRoot.Path, f))
            .ToList();

        Assert.True(offenders.Count == 0,
            "SECRETSCAN_01 — private key / certificate / password files are visible to git. Add them to "
          + ".gitignore (and remove them from the index with `git rm --cached`):"
          + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void GitIgnoreCoversSecretFileTypes()
    {
        string root = File.ReadAllText(Path.Combine(RepoRoot.Path, ".gitignore"));
        foreach (string pattern in new[] { "*.pfx", "*.p12", "*.key.pem", "*.password.txt", ".env" })
            Assert.Contains(pattern, root);
    }
}
