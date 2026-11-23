using System;

namespace FreeVideoStudio.Core.Infrastructure;

public static class CoreLogger
{
    public static Action<string, string>? InfoAction;
    public static Action<string, string>? FailAction;
    public static Action<string, string>? DebugAction;

    public static Action<string, string>? WarnAction;

    public static Action<string>? AppendAction;

    public static void Info(string step, string detail)
    {
        InfoAction?.Invoke(step, detail);
    }

    public static void Debug(string step, string detail)
    {
        DebugAction?.Invoke(step, detail);
    }

    public static void Warn(string step, string detail)
    {
        if (WarnAction != null) WarnAction(step, detail);
        else DebugAction?.Invoke(step, detail);
    }

    public static void Fail(string step, string detail)
    {
        FailAction?.Invoke(step, detail);
    }

    public static void Append(string line)
    {
        AppendAction?.Invoke(line);
    }

    public static void Swallowed(
        Exception ex,
        [System.Runtime.CompilerServices.CallerMemberName] string member = "",
        [System.Runtime.CompilerServices.CallerFilePath] string file = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        try
        {
            string where = $"{System.IO.Path.GetFileName(file)}:{line} {member}()";

            Abstractions.Faults.Recoverable("SWALLOWED", $"{where} — {ex.GetType().Name}: {ex.Message}", ex);
        }
        catch (Exception)
        {
        }
    }
}
