using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FvsVerify;
using Xunit;

namespace FvsVerify.Tests;

public sealed class SentinelListTests
{
    [Fact]
    public void DevVerificationPrecedesCleanupAndHasAStandaloneMode()
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot, "dev.cmd"));
        int verify = script.IndexOf("dotnet run --project", StringComparison.Ordinal);
        int halt = script.IndexOf("if errorlevel 1 exit /b 1", StringComparison.Ordinal);
        int standalone = script.IndexOf("if /I \"%~1\"==\"verify\" exit /b 0", StringComparison.Ordinal);
        Assert.True(verify >= 0 && halt > verify && standalone > halt);
        Assert.True(script.IndexOf("powershell -NoProfile", StringComparison.Ordinal) > standalone);
        Assert.DoesNotContain("taskkill", script);
        Assert.DoesNotContain("rd /s", script);
    }

    private static string RepoRoot =>
        SentinelList.FindRepositoryRoot(AppContext.BaseDirectory)
        ?? throw new InvalidOperationException("Could not locate the repository root from the test binary.");

    [Fact]
    public void EveryFixSentinelStillResolves()
    {
        IReadOnlyList<Sentinel> sentinels = SentinelList.Load(RepoRoot, out IReadOnlyList<string> malformed);
        IReadOnlyList<SentinelResult> failures = SentinelList.Check(RepoRoot, sentinels);

        Assert.True(malformed.Count == 0 && failures.Count == 0,
            SentinelList.FormatFailures(malformed, failures));
    }

    [Fact]
    public void TheListIsNotEmpty()
    {
        IReadOnlyList<Sentinel> sentinels = SentinelList.Load(RepoRoot, out _);
        Assert.True(sentinels.Count >= 100,
            $"Only {sentinels.Count} sentinels parsed out of {SentinelList.RelativePath}. The list held 164 "
          + "when it moved out of dev.cmd. A sudden drop means the format changed and entries are "
          + "being skipped, not that the fixes were retired.");
    }

    [Fact]
    public void CommentsAndBlankLinesAreSkipped()
    {
        IReadOnlyList<Sentinel> parsed = SentinelList.Parse(new[]
        {
            "# a comment with (round brackets), \"quotes\" and an = sign",
            "",
            "   ",
            "# RETIRED OLDTAG_01 — superseded by a rewrite",
            "TAG_01=src/Thing.cs",
        }, out IReadOnlyList<string> malformed);

        Assert.Empty(malformed);
        Sentinel only = Assert.Single(parsed);
        Assert.Equal("TAG_01", only.Tag);
        Assert.Equal("src/Thing.cs", only.RelativePath);
    }

    [Fact]
    public void AMalformedLineIsReportedRatherThanSkipped()
    {
        IReadOnlyList<Sentinel> parsed = SentinelList.Parse(new[]
        {
            "TAG_01=src/Thing.cs",
            "REM --- Crop Tools rework, phase 3 ---",
            "=src/NoTag.cs",
            "TAG_02=",
        }, out IReadOnlyList<string> malformed);

        Assert.Single(parsed);
        Assert.Equal(3, malformed.Count);
    }

    [Fact]
    public void ATagWithWhitespaceIsMalformed()
    {
        SentinelList.Parse(new[] { "TAG 01=src/Thing.cs" }, out IReadOnlyList<string> malformed);
        Assert.Single(malformed);
    }

    [Fact]
    public void ABackslashPathIsMalformed()
    {
        SentinelList.Parse(new[] { @"TAG_01=src\Thing.cs" }, out IReadOnlyList<string> malformed);
        string only = Assert.Single(malformed);
        Assert.Contains("forward slashes", only, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameTagAndPathTwiceIsCheckedOnce()
    {
        IReadOnlyList<Sentinel> parsed = SentinelList.Parse(new[]
        {
            "TAG_01=src/Thing.cs",
            "TAG_01=src/Thing.cs",
            "TAG_01=src/Other.cs",
        }, out _);

        Assert.Equal(2, parsed.Count);
    }

    [Fact]
    public void AMissingFileAndAMissingTagAreDistinguished()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fvsverify_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "src", "Present.cs"), "// nothing of interest here");

        IReadOnlyList<SentinelResult> failures = SentinelList.Check(dir, new[]
        {
            new Sentinel("TAG_01", "src/Present.cs", 1),
            new Sentinel("TAG_02", "src/Absent.cs", 2),
        });

        try
        {
            Assert.Equal(2, failures.Count);
            Assert.Equal(SentinelFailure.TagMissing, failures[0].Failure);
            Assert.Equal(SentinelFailure.FileMissing, failures[1].Failure);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void APresentTagPasses()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fvsverify_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "src", "Present.cs"), "// TAG_01 — the fix this guards\nint x = 1;");

        try
        {
            Assert.Empty(SentinelList.Check(dir, new[] { new Sentinel("TAG_01", "src/Present.cs", 1) }));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TheFailureMessageNamesTagFileReasonAndListLine()
    {
        string text = SentinelList.FormatFailures(
            Array.Empty<string>(),
            new[] { new SentinelResult(new Sentinel("ZOOMLIVE_07", "src/Editor.cs", 42), SentinelFailure.TagMissing) });

        Assert.Contains("ZOOMLIVE_07", text, StringComparison.Ordinal);
        Assert.Contains("src/Editor.cs", text, StringComparison.Ordinal);
        Assert.Contains("tag not found", text, StringComparison.Ordinal);
        Assert.Contains(":42", text, StringComparison.Ordinal);
        Assert.Contains("RETIRED", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSymbolFormIsParsed()
    {
        IReadOnlyList<Sentinel> parsed = SentinelList.Parse(new[] { "TAG_01=src/A.cs#DescribeSourceRange" }, out IReadOnlyList<string> malformed);

        Assert.Empty(malformed);
        Sentinel s = Assert.Single(parsed);
        Assert.Equal("TAG_01", s.Tag);
        Assert.Equal("src/A.cs", s.RelativePath);
        Assert.Equal("DescribeSourceRange", s.Symbol);
    }

    [Theory]
    [InlineData("TAG_01=src/A.cs#")]
    [InlineData("TAG_01=src/A.cs#Not An Identifier")]
    [InlineData("TAG_01=src/A.cs#Name.Member")]
    public void AnEmptyOrInvalidSymbolIsMalformed(string line)
    {
        IReadOnlyList<Sentinel> parsed = SentinelList.Parse(new[] { line }, out IReadOnlyList<string> malformed);

        Assert.Empty(parsed);
        Assert.Single(malformed);
    }

    [Fact]
    public void ASymbolSentinelChecksTheSymbolNotTheTag()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fvsverify_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "src", "A.cs"), "public static class A { public static int DescribeSourceRange() => 1; }");

        try
        {
            IReadOnlyList<SentinelResult> failures = SentinelList.Check(dir, new[]
            {
                new Sentinel("TAG_01", "src/A.cs", 1, "DescribeSourceRange"),
                new Sentinel("TAG_02", "src/A.cs", 2, "DescribeSource"),
            });

            SentinelResult f = Assert.Single(failures);
            Assert.Equal("TAG_02", f.Sentinel.Tag);
            Assert.Equal(SentinelFailure.SymbolMissing, f.Failure);
            Assert.Contains("symbol DescribeSource not found", SentinelList.FormatFailures(Array.Empty<string>(), failures), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
