using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FvsComments;

public readonly record struct ExternalToken(string Kind, int Start, int End);

public sealed class ExternalTokenizers : IDisposable
{
    private const string PowerShellScript = """
        param([string]$Path)
        $ErrorActionPreference = 'Stop'
        $text = [System.IO.File]::ReadAllText($Path, (New-Object System.Text.UTF8Encoding($false)))
        $tokens = $null
        $errors = $null
        [void][System.Management.Automation.Language.Parser]::ParseInput($text, [ref]$tokens, [ref]$errors)
        if ($errors.Count -gt 0) { [Console]::Error.WriteLine($errors[0].Message); exit 3 }
        $sb = New-Object System.Text.StringBuilder
        foreach ($t in $tokens) { [void]$sb.Append($t.Kind).Append("`t").Append($t.Extent.StartOffset).Append("`t").Append($t.Extent.EndOffset).Append("`n") }
        [Console]::Out.Write($sb.ToString())
        """;

    private const string PythonScript = """
        import io, sys, tokenize
        text = open(sys.argv[1], encoding='utf-8', newline='').read()
        reader = io.StringIO(text, newline='')
        lines = []
        while True:
            line = reader.readline()
            if not line:
                break
            lines.append(line)
        starts = [0]
        for line in lines:
            starts.append(starts[-1] + len(line.encode('utf-16-le')) // 2)
        def offset(row, col):
            if row - 1 >= len(lines):
                return starts[-1]
            return starts[row - 1] + len(lines[row - 1][:col].encode('utf-16-le')) // 2
        out = []
        for tok in tokenize.generate_tokens(io.StringIO(text, newline='').readline):
            out.append('%s\t%d\t%d' % (tokenize.tok_name[tok.type], offset(*tok.start), offset(*tok.end)))
        sys.stdout.write('\n'.join(out))
        """;

    private readonly string _work = Path.Combine(Path.GetTempPath(), "fvscomments-" + Guid.NewGuid().ToString("N"));
    private string? _pwsh;
    private string? _python;

    public ExternalTokenizers()
    {
        PowerShellLanguage = new PowerShellCommentLanguage(this);
        PythonLanguage = new PythonCommentLanguage(this);
    }

    public CommentLanguage PowerShellLanguage { get; }

    public CommentLanguage PythonLanguage { get; }

    public IReadOnlyList<ExternalToken> PowerShell(string text)
    {
        _pwsh ??= FirstWorking(new[] { "pwsh", "powershell" }, "-NoProfile -NonInteractive -Command exit 0")
                  ?? throw new InvalidOperationException("PowerShell (pwsh or powershell.exe) is needed to read .ps1 files and was not found.");
        string script = Write("tokens.ps1", PowerShellScript);
        return Run(_pwsh, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\" \"{Write("input.ps1", text)}\"", "PowerShell");
    }

    public IReadOnlyList<ExternalToken> Python(string text)
    {
        _python ??= FirstWorking(new[] { "python", "py" }, "-I -B -c \"import tokenize\"")
                    ?? throw new InvalidOperationException("Python is needed to read .py files and was not found.");
        string script = Write("tokens.py", PythonScript);
        return Run(_python, $"-I -B \"{script}\" \"{Write("input.py", text)}\"", "Python");
    }

    private string Write(string name, string content)
    {
        Directory.CreateDirectory(_work);
        string path = Path.Combine(_work, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static string? FirstWorking(string[] candidates, string probe)
    {
        foreach (string exe in candidates)
        {
            try
            {
                using Process? p = Process.Start(new ProcessStartInfo(exe, probe)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                });
                if (p is null) continue;
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                if (p.WaitForExit(60_000) && p.ExitCode == 0) return exe;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                continue;
            }
        }
        return null;
    }

    private static IReadOnlyList<ExternalToken> Run(string exe, string arguments, string what)
    {
        var psi = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using Process p = Process.Start(psi) ?? throw new InvalidOperationException($"{what} could not be started.");
        var errorTask = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(120_000))
        {
            p.Kill(true);
            throw new InvalidOperationException($"{what} tokenizer timed out; nothing was written for this file.");
        }
        string error = errorTask.Result.Trim();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"{what} could not parse the file ({FirstLine(error)}); nothing was written for this file.");
        var tokens = new List<ExternalToken>();
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] f = line.TrimEnd('\r').Split('\t');
            if (f.Length != 3 || !int.TryParse(f[1], out int s) || !int.TryParse(f[2], out int e))
                throw new InvalidOperationException($"{what} tokenizer printed an unexpected line; nothing was written for this file.");
            tokens.Add(new ExternalToken(f[0], s, e));
        }
        return tokens;
    }

    private static string FirstLine(string s) => s.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "no details";

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_work)) Directory.Delete(_work, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Find_and_Remove_Comments: could not delete its temporary folder {_work}.");
        }
    }
}

public sealed class PowerShellCommentLanguage : CommentLanguage
{
    private static readonly Regex Functional = new(@"^#(?:requires\b|\s*SIG\s*#)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly ExternalTokenizers _external;

    public PowerShellCommentLanguage(ExternalTokenizers external) => _external = external;

    public override string Name => "PowerShell";

    public override bool JoinWithSpace => true;

    public override Scan Find(string text)
    {
        var comments = new List<Range>();
        var guarded = new List<Range>();
        foreach (ExternalToken t in _external.PowerShell(text).Where(t => t.Kind != "EndOfInput"))
        {
            if (t.Start < 0 || t.End > text.Length || t.End < t.Start)
                throw new InvalidOperationException("PowerShell token offsets are out of range; nothing was written for this file.");
            if (t.Kind == "Comment")
            {
                if (!Functional.IsMatch(text[t.Start..t.End])) comments.Add(new Range(t.Start, t.End));
            }
            else if (t.Kind.Contains("String", StringComparison.Ordinal) && text.AsSpan(t.Start, t.End - t.Start).IndexOfAny('\r', '\n') >= 0)
            {
                guarded.Add(new Range(t.Start, t.End));
            }
        }
        return new Scan(comments, guarded);
    }

    public override void Verify(string before, string after) =>
        RequireEqual(Significant(before), Significant(after), "PowerShell tokens");

    private List<string> Significant(string text)
    {
        var result = new List<string>();
        foreach (ExternalToken t in _external.PowerShell(text).Where(t => t.Kind != "EndOfInput"))
        {
            string body = text[t.Start..t.End];
            if (t.Kind == "Comment" && !Functional.IsMatch(body)) continue;
            if (t.Kind is "NewLine" or "LineContinuation")
            {
                if (result.Count == 0 || result[^1] == "NewLine") continue;
                result.Add("NewLine");
                continue;
            }
            result.Add(t.Kind + "|" + body);
        }
        while (result.Count > 0 && result[^1] == "NewLine") result.RemoveAt(result.Count - 1);
        return result;
    }

    public override string CleanLine(string line)
    {
        if (line.StartsWith("<#", StringComparison.Ordinal)) line = line[2..];
        if (line.EndsWith("#>", StringComparison.Ordinal)) line = line[..^2];
        return NoteBuilder.StripPrefix(line.Trim(), "#");
    }
}

public sealed class PythonCommentLanguage : CommentLanguage
{
    private static readonly Regex Coding = new(@"^#.*?coding[:=]\s*[-\w.]+", RegexOptions.Compiled);
    private readonly ExternalTokenizers _external;

    public PythonCommentLanguage(ExternalTokenizers external) => _external = external;

    public override string Name => "Python";

    public override int MaxBlank(string previous, string next) => TextRewriter.Indent(next) == 0 ? 2 : 1;

    public override Scan Find(string text)
    {
        var comments = new List<Range>();
        var guarded = new List<Range>();
        int fstringStart = -1;
        int depth = 0;
        foreach (ExternalToken t in _external.Python(text))
        {
            if (t.Start < 0 || t.End > text.Length || t.End < t.Start)
                throw new InvalidOperationException("Python token offsets are out of range; nothing was written for this file.");
            switch (t.Kind)
            {
                case "COMMENT":
                    if (!IsFunctional(text, t)) comments.Add(new Range(t.Start, t.End));
                    break;
                case "STRING":
                    if (text.AsSpan(t.Start, t.End - t.Start).IndexOfAny('\r', '\n') >= 0) guarded.Add(new Range(t.Start, t.End));
                    break;
                case "FSTRING_START":
                    if (depth++ == 0) fstringStart = t.Start;
                    break;
                case "FSTRING_END":
                    if (--depth == 0 && text.AsSpan(fstringStart, t.End - fstringStart).IndexOfAny('\r', '\n') >= 0)
                        guarded.Add(new Range(fstringStart, t.End));
                    break;
            }
        }
        return new Scan(comments, guarded);
    }

    private static bool IsFunctional(string text, ExternalToken t)
    {
        string body = text[t.Start..t.End];
        int line = text[..t.Start].Count(c => c == '\n') + 1;
        return (line == 1 && body.StartsWith("#!", StringComparison.Ordinal)) || (line <= 2 && Coding.IsMatch(body));
    }

    public override void Verify(string before, string after) =>
        RequireEqual(Significant(before), Significant(after), "Python tokens");

    private List<string> Significant(string text) =>
        _external.Python(text)
            .Where(t => t.Kind is not ("COMMENT" or "NL" or "ENCODING") || (t.Kind == "COMMENT" && IsFunctional(text, t)))
            .Select(t => t.Kind == "NEWLINE" ? "NEWLINE" : t.Kind + "|" + text[t.Start..t.End])
            .ToList();

    public override string CleanLine(string line) => NoteBuilder.StripPrefix(line, "#");
}
