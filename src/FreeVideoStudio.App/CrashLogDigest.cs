
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App;

/// <summary>
/// On startup, sweeps the Windows Application event log for THIS app's crash / hang
/// entries (Application Error, .NET Runtime, Application Hang, Windows Error Reporting)
/// that occurred since the last sweep and folds them into RuntimeLog — so native crashes
/// that only Windows Error Reporting saw (the ones you'd otherwise dig out of Event Viewer
/// by hand) end up in the same .log file as everything else.
///
/// NOHIDDENTOOLS_01 — reads the log through the Windows Event Log API (wevtapi.dll, direct
/// P/Invoke: NativeAOT-safe, unlike the reflection-heavy <c>System.Diagnostics.EventLog</c>). It used
/// to start the hidden Windows event tool on EVERY launch; a program that starts hidden system tools
/// at startup is what antivirus behaviour rules score (SYS-AVCLEAN). Reading the Application log does
/// not require elevation. Spec: docs/05_SYSTEM_LIFECYCLE_STORAGE.md #SYS-AVCLEAN.
/// </summary>
internal static partial class CrashLogDigest
{
    /// <summary>Fire-and-forget: runs off the UI/startup thread so it never delays launch.</summary>
    public static Task RunAsync() => Task.Run(Run);

    private static void Run()
    {
        try
        {
            string markerPath = RuntimeLog.LogPath + ".crashdigest";
            DateTime sinceUtc = ReadMarker(markerPath);
            DateTime sweepStartedUtc = DateTime.UtcNow;

            string exe = Path.GetFileName(Environment.ProcessPath ?? "FreeVideoStudio.exe");
            string sinceIso = sinceUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

            string query =
                "*[System[(Provider[@Name='Application Error'] or Provider[@Name='.NET Runtime'] " +
                "or Provider[@Name='Application Hang'] or Provider[@Name='Windows Error Reporting']) " +
                $"and TimeCreated[@SystemTime>='{sinceIso}']]]";

            int ingested = 0;
            foreach (string xml in ReadEvents(query, MaxEvents))
            {
                if (!Mentions(xml, exe)) continue;
                RuntimeLog.Fail("EVENTLOG CRASH", CondenseXml(xml));
                ingested++;
            }

            if (ingested > 0)
                RuntimeLog.Info("EVENTLOG DIGEST",
                    $"Folded {ingested} crash/error event(s) from Windows Event Viewer (since {sinceIso}) into this log.");

            WriteMarker(markerPath, sweepStartedUtc);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("EVENTLOG DIGEST", $"Crash log digest failed completely: {ex.Message}");
        }
    }

    private const int MaxEvents = 100;
    private const uint EvtQueryChannelPath = 0x1;
    private const uint EvtQueryReverseDirection = 0x200;
    private const uint EvtRenderEventXml = 1;
    private const int ErrorInsufficientBuffer = 122;

    [LibraryImport("wevtapi.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint EvtQuery(nint session, string path, string query, uint flags);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool EvtNext(nint resultSet, uint eventsSize, nint* events, uint timeoutMs, uint flags, uint* returned);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool EvtRender(nint context, nint fragment, uint flags, uint bufferBytes, char* buffer, uint* bufferUsed, uint* propertyCount);

    [LibraryImport("wevtapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtClose(nint handle);

    /// <summary>
    /// NOHIDDENTOOLS_01 — the newest <paramref name="max"/> events of the Application log that match
    /// <paramref name="query"/>, each as its event XML. Empty when the log cannot be read: a crash
    /// digest must never disturb a launch.
    /// </summary>
    internal static unsafe List<string> ReadEvents(string query, int max)
    {
        var events = new List<string>();
        if (!OperatingSystem.IsWindows()) return events;
        nint results = EvtQuery(0, "Application", query, EvtQueryChannelPath | EvtQueryReverseDirection);
        if (results == 0)
        {
            RuntimeLog.Info("EVENTLOG DIGEST", $"The Windows event log could not be queried (error {Marshal.GetLastPInvokeError()}).");
            return events;
        }
        try
        {
            const int Batch = 16;
            nint* handles = stackalloc nint[Batch];
            while (events.Count < max)
            {
                uint returned = 0;
                if (!EvtNext(results, Batch, handles, 5000, 0, &returned) || returned == 0) break;
                for (int i = 0; i < returned; i++)
                {
                    try
                    {
                        if (events.Count < max && RenderXml(handles[i]) is { Length: > 0 } xml) events.Add(xml);
                    }
                    finally { EvtClose(handles[i]); }
                }
            }
        }
        finally { EvtClose(results); }
        return events;
    }

    private static unsafe string? RenderXml(nint eventHandle)
    {
        uint used = 0, properties = 0;
        if (EvtRender(0, eventHandle, EvtRenderEventXml, 0, null, &used, &properties)) return null;
        if (Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer || used == 0 || used > 1_000_000) return null;
        char[] buffer = new char[(used / 2) + 1];
        fixed (char* p = buffer)
        {
            if (!EvtRender(0, eventHandle, EvtRenderEventXml, (uint)(buffer.Length * 2), p, &used, &properties)) return null;
        }
        return new string(buffer, 0, Math.Min(buffer.Length, (int)(used / 2))).TrimEnd((char)0);
    }

    /// <summary>True when the event names this app or one of its media helpers (pure, unit-tested).</summary>
    internal static bool Mentions(string xml, string exe) =>
        xml.Contains("FreeVideoStudio", StringComparison.OrdinalIgnoreCase)
        || xml.Contains(exe, StringComparison.OrdinalIgnoreCase)
        || xml.Contains("ffmpeg.exe", StringComparison.OrdinalIgnoreCase)
        || xml.Contains("ffprobe.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Collapses one event's XML into a readable line: source, event number, time, then the values
    /// Windows recorded (faulting program, module, exception code, …). Pure, unit-tested. XML that
    /// cannot be read is kept as shortened text rather than lost.
    /// </summary>
    internal static string CondenseXml(string xml)
    {
        const int cap = 1200;
        var keep = new List<string>();
        try
        {
            XElement root = XDocument.Parse(xml).Root!;
            XElement? system = root.Elements().FirstOrDefault(e => e.Name.LocalName == "System");
            string? provider = system?.Elements().FirstOrDefault(e => e.Name.LocalName == "Provider")?.Attribute("Name")?.Value;
            string? id = system?.Elements().FirstOrDefault(e => e.Name.LocalName == "EventID")?.Value;
            string? time = system?.Elements().FirstOrDefault(e => e.Name.LocalName == "TimeCreated")?.Attribute("SystemTime")?.Value;
            if (!string.IsNullOrWhiteSpace(provider)) keep.Add("Source: " + provider);
            if (!string.IsNullOrWhiteSpace(id)) keep.Add("Event ID: " + id);
            if (!string.IsNullOrWhiteSpace(time)) keep.Add("Date: " + time);
            foreach (XElement data in root.Descendants().Where(e => e.Name.LocalName == "Data"))
            {
                string value = string.Join(" ", data.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                if (value.Length > 0) keep.Add(value);
            }
        }
        catch (System.Xml.XmlException ex)
        {
            RuntimeLog.Swallowed(ex);
            keep.Clear();
            keep.Add(string.Join(" ", xml.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        }

        string joined = string.Join(" | ", keep);
        if (joined.Length > cap) joined = joined.Substring(0, cap) + " …(truncated)";
        return joined;
    }

    private static DateTime ReadMarker(string path)
    {
        try
        {
            using var mutex = new System.Threading.Mutex(false, FreeVideoStudio.Core.Infrastructure.NamedSystemMutex.UserScopedName("FreeVideoStudioCrashDigestMutex"));
            bool acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(2000); } catch (System.Threading.AbandonedMutexException swallowed3)
                {
                    acquired = true;
                    global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed3);
                }
                if (File.Exists(path))
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var sr = new StreamReader(fs, Encoding.UTF8);
                    string s = sr.ReadToEnd().Trim();
                    if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime dt))
                        return dt.ToUniversalTime();
                }
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }
        catch (Exception ex)
        {
            RuntimeLog.Info("EVENTLOG DIGEST", $"ReadMarker failed, falling back to 3 days ago: {ex.Message}");
        }
        return DateTime.UtcNow.AddDays(-3);
    }

    private static void WriteMarker(string path, DateTime utc)
    {
        try
        {
            using var mutex = new System.Threading.Mutex(false, FreeVideoStudio.Core.Infrastructure.NamedSystemMutex.UserScopedName("FreeVideoStudioCrashDigestMutex"));
            bool acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(2000); } catch (System.Threading.AbandonedMutexException swallowed2)
                {
                    acquired = true;
                    global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed2);
                }
                using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                using var sw = new StreamWriter(fs, new UTF8Encoding(false));
                sw.Write(utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
                sw.Flush();
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }
        catch (Exception ex)
        {
            RuntimeLog.Info("EVENTLOG DIGEST", $"WriteMarker failed: {ex.Message}");
        }
    }
}
