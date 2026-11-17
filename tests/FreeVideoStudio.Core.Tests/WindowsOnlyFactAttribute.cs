using System;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows-only: spawns cmd.exe / powershell.exe. Runs in CI on windows-latest.";
    }
}
