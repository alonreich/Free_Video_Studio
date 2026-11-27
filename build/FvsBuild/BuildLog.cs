using FreeVideoStudio.Core.Infrastructure;

namespace FvsBuild;

internal sealed class BuildLog : IDisposable
{
    private enum Level { Info, Warning, Error, Success, Banner }

    private readonly string _path;
    private readonly BoundedLogFile _writer;
    private readonly object _gate = new();
    private int _errorCount;
    private int _warningCount;

    public BuildLog(string path)
    {
        _path = path;
        try { if (File.Exists("build.result.txt")) File.Delete("build.result.txt"); } catch (IOException) { }
        BoundedLogFile.Replace(path, string.Empty);
        _writer = new BoundedLogFile(path);
    }

    public int ErrorCount { get { lock (_gate) return _errorCount; } }
    public int WarningCount { get { lock (_gate) return _warningCount; } }

    public void Info(string line) => Write(line, Level.Info);
    public void Warn(string line) => Write(line, Level.Warning);
    public void Error(string line) => Write(line, Level.Error);
    public void Success(string line) => Write(line, Level.Success);
    public void Banner(string line) => Write(line, Level.Banner);

    private void Write(string line, Level level)
    {
        lock (_gate)
        {
            if (level == Level.Error) _errorCount++;
            if (level == Level.Warning) _warningCount++;
            try
            {
                ConsoleColor previous = Console.ForegroundColor;
                Console.ForegroundColor = level switch
                {
                    Level.Error => ConsoleColor.Red,
                    Level.Warning => ConsoleColor.Yellow,
                    Level.Success => ConsoleColor.Green,
                    Level.Banner => ConsoleColor.Cyan,
                    _ => previous,
                };
                Console.WriteLine(line);
                Console.ForegroundColor = previous;
            }
            catch (IOException)
            {
                Console.WriteLine(line);
            }
            _writer.Write(line + Environment.NewLine);
        }
    }

    public void WriteVerdictAndClose(int exitCode, TimeSpan duration)
    {
        string label = exitCode != 0 || ErrorCount > 0 ? "FAIL"
            : WarningCount > 0 ? "WARN"
            : "OK";
        string detail = label switch
        {
            "FAIL" => $"- exit code {exitCode}",
            "WARN" => $"- {WarningCount} warning line(s)",
            _ => "- clean success",
        };
        string first = $"{label} {detail} - Free Video Studio build {DateTime.Now:yyyy-MM-dd HH:mm:ss} " +
                       $"({(int)duration.TotalMinutes}m{duration.Seconds:D2}s)";
        lock (_gate)
        {
            BoundedLogFile.Prepend(_path, first + Environment.NewLine);
        }
    }

    public void Dispose()
    {
        lock (_gate) _writer.Dispose();
    }
}
