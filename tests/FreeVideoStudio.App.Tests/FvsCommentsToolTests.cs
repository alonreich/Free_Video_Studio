using FreeVideoStudio.Testing;
using FvsComments;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class FvsCommentsToolTests
{
    private static string Strip(CommentLanguage language, string text) => language.Strip(text).Text;

    [Fact]
    public void CSharpLosesCommentsAndExtraBlankLines()
    {
        string input = "\n\nnamespace A;\n\n\n// note\npublic class B\n{\n\n    int x; // tail\n\n}\n\n";
        Assert.Equal("namespace A;\n\npublic class B\n{\n    int x;\n}\n", CommentScanner.Strip(input).Text);
    }

    [Fact]
    public void CSharpKeepsBlankLinesInsideStrings()
    {
        string input = "class C\n{\n    string s = \"\"\"\n        a\n\n\n        b\n        \"\"\";\n}\n";
        Assert.Equal(input, CommentScanner.Strip(input).Text);
    }

    [Fact]
    public void XmlLosesCommentsButNotCommentLookingAttributeText()
    {
        string input = "<Root>\n  <!-- note -->\n  <A V=\"&lt;!-- not --&gt;\" />\n\n\n  <B><![CDATA[<!-- kept -->]]></B>\n</Root>\n";
        Assert.Equal("<Root>\n  <A V=\"&lt;!-- not --&gt;\" />\n\n  <B><![CDATA[<!-- kept -->]]></B>\n</Root>\n", Strip(new XmlLanguage(), input));
    }

    [Fact]
    public void BatchLosesRemAndDoubleColonLinesButKeepsLabels()
    {
        string input = "@echo off\r\nREM one\r\n:: two\r\n:label\r\n\r\n\r\necho hi\r\n";
        Assert.Equal("@echo off\r\n:label\r\n\r\necho hi\r\n", Strip(new BatchLanguage(), input));
    }

    [Fact]
    public void YamlKeepsHashLinesInsideBlockScalars()
    {
        string input = "# top\non: push\njobs:\n  a:\n    steps:\n      - run: |\n          # shell line\n\n\n          echo hi\n";
        Assert.Equal("on: push\njobs:\n  a:\n    steps:\n      - run: |\n          # shell line\n\n\n          echo hi\n", Strip(new YamlLanguage(), input));
    }

    [Fact]
    public void InnoLosesSemicolonAndPascalCommentsButKeepsDirectivesAndStrings()
    {
        string input = "; top\n#define X 1\n[Setup]\nAppName={#X}\n[Code]\n{ note }\nfunction F: Boolean;\nbegin\n  {$IFDEF A}\n  Result := '{x}'; // tail\nend;\n";
        Assert.Equal("#define X 1\n[Setup]\nAppName={#X}\n[Code]\nfunction F: Boolean;\nbegin\n  {$IFDEF A}\n  Result := '{x}';\nend;\n", Strip(new InnoLanguage(), input));
    }

    [Fact]
    public void IgnoreFilesLoseOnlyLinesThatStartWithHash()
    {
        Assert.Equal("bin/\n #pattern\n", Strip(new HashLineLanguage(), "# note\nbin/\n #pattern\n"));
    }

    [Fact]
    public void DocumentationIsNeverTouched()
    {
        foreach (string path in new[] { "docs/05_SYSTEM_LIFECYCLE_STORAGE.md", "README.md", "project_structure.txt", "version.txt", "LICENSE" })
            Assert.True(Languages.IsDocumentation(path), path);
        foreach (string path in new[] { "src/FreeVideoStudio.App/App.cs", "build/sentinels.txt", "build/revoked-signers.txt" })
            Assert.False(Languages.IsDocumentation(path), path);
    }

    [Fact]
    public void CSharpLosesCommentsInsideEveryConditionalBranch()
    {
        string input = "class C\n{\n#if RELEASE\n    // a\n    string s = @\"x\n\n// kept\";\n#elif DEBUG\n    // b\n#else\n    /* c */\n#endif\n#if false\n    // d\n#endif\n}\n";
        Assert.Equal("class C\n{\n#if RELEASE\n    string s = @\"x\n\n// kept\";\n#elif DEBUG\n#else\n#endif\n#if false\n#endif\n}\n", CommentScanner.Strip(input).Text);
        Assert.Equal(4, CommentScanner.CountComments(input));
    }

    [Fact]
    public void CSharpLosesRegionLinesButNotRegionLookingStrings()
    {
        string input = "class C\n{\n    #region Label\n    int x;\n    #endregion // tail\n    string s = \"#region no\";\n}\n";
        StripResult r = CommentScanner.Strip(input);
        Assert.Equal("class C\n{\n    int x;\n    string s = \"#region no\";\n}\n", r.Text);
        Assert.Contains(r.Notes, n => n.Text.Contains("Label", StringComparison.Ordinal));
        Assert.Equal(1, CommentScanner.CountComments(input));
    }

    [Fact]
    public void BatchLosesRemWithAnyDelimiterAndInlineRemAfterAmpersand()
    {
        string input = "rem.a\r\nrem:b\r\necho x & rem c\r\necho y&&:: d\r\ndir 2>&1 & rem e\r\necho \"q & rem f\"\r\necho ^& rem g\r\necho z || rem h\r\nremove.exe\r\n";
        Assert.Equal("echo x\r\necho y\r\ndir 2>&1\r\necho \"q & rem f\"\r\necho ^& rem g\r\necho z || rem h\r\nremove.exe\r\n", Strip(new BatchLanguage(), input));
    }

    [Fact]
    public void DataListsLoseIndentedHashLinesButKeepSentinelTagsAndHashSymbols()
    {
        string input = "# REVOKE_01 header\n  # note\nA_01=src/a.cs#Name\n";
        Assert.Equal("# REVOKE_01 header\nA_01=src/a.cs#Name\n", Strip(Languages.DataList(new[] { "REVOKE_01" }), input));
        Assert.Equal("A_01=src/a.cs#Name\n", Strip(Languages.DataList(Array.Empty<string>()), input));
    }

    [Fact]
    public void SolutionKeepsItsHeaderAndBlankFirstLine()
    {
        string input = "\r\nMicrosoft Visual Studio Solution File, Format Version 12.00\r\n# Visual Studio Version 17\r\n# note\r\nVisualStudioVersion = 17.0\r\n";
        using var external = new ExternalTokenizers();
        var sln = Languages.For("FreeVideoStudio.sln", external)!;
        Assert.Equal("\r\nMicrosoft Visual Studio Solution File, Format Version 12.00\r\n# Visual Studio Version 17\r\nVisualStudioVersion = 17.0\r\n", Strip(sln, input));
    }

    [Fact]
    public void JsonAndEditorConfigLoseComments()
    {
        using var external = new ExternalTokenizers();
        string json = "{\n  // a\n  \"u\": \"http://x // no\",\n  \"v\": \"\\\"/* no */\"\n}\n";
        Assert.Equal("{\n  \"u\": \"http://x // no\",\n  \"v\": \"\\\"/* no */\"\n}\n", Strip(Languages.For("a.json", external)!, json));
        Assert.Equal("root = true\n", Strip(Languages.For(".editorconfig", external)!, "# a\n; b\nroot = true\n"));
    }

    [Fact]
    public void NotesForTestsGoToTheirOwnBucket()
    {
        Assert.Equal("Tests", Runner.Bucket("tests/FreeVideoStudio.App.Tests/A.cs"));
        Assert.Equal("App", Runner.Bucket("src/FreeVideoStudio.App/A.axaml"));
        Assert.Equal("Core", Runner.Bucket("src/FreeVideoStudio.Core/A.cs"));
        Assert.Equal("Tools", Runner.Bucket("build/FvsBuild/A.cs"));
    }

    [Fact]
    public void CommitWritesNothingWhenAFileChangedMeanwhile()
    {
        string folder = Path.Combine(TestArtifactDirectory.SuiteWork(TestOutputSuite.Name), "FvsComments", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string a = Path.Combine(folder, "a.cs");
        string b = Path.Combine(folder, "b.cs");
        File.WriteAllText(a, "old a");
        File.WriteAllText(b, "changed b");
        var staged = new[]
        {
            new Staged(a, "new a"u8.ToArray(), "old a"u8.ToArray()),
            new Staged(b, "new b"u8.ToArray(), "old b"u8.ToArray()),
        };
        Assert.Throws<InvalidOperationException>(() => Runner.Commit(staged));
        Assert.Equal("old a", File.ReadAllText(a));
        Assert.Equal("changed b", File.ReadAllText(b));
        Assert.Equal(new[] { "a.cs", "b.cs" }, Directory.GetFiles(folder).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void CommitReplacesEveryFileAndLeavesNoScratch()
    {
        string folder = Path.Combine(TestArtifactDirectory.SuiteWork(TestOutputSuite.Name), "FvsComments", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string a = Path.Combine(folder, "a.cs");
        string notes = Path.Combine(folder, "notes.md");
        File.WriteAllText(a, "old a");
        Runner.Commit(new[]
        {
            new Staged(a, "new a"u8.ToArray(), "old a"u8.ToArray()),
            new Staged(notes, "notes"u8.ToArray(), null),
        });
        Assert.Equal("new a", File.ReadAllText(a));
        Assert.Equal("notes", File.ReadAllText(notes));
        Assert.Equal(2, Directory.GetFiles(folder).Length);
    }
}
