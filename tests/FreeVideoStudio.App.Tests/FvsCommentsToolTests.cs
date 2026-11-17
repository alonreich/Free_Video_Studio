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
        foreach (string path in new[] { "docs/05_SYSTEM_LIFECYCLE_STORAGE.md", "README.md", "build/sentinels.txt", "LICENSE" })
            Assert.True(Languages.IsDocumentation(path), path);
        Assert.False(Languages.IsDocumentation("src/FreeVideoStudio.App/App.cs"));
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
