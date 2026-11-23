using System;
using System.Collections.Generic;

namespace FreeVideoStudio.Core.Media;

public sealed class RenderedMixGate
{
    private long _generation;
    private long _sigChangedAtMs;

    public readonly record struct Ticket(long Generation, string Signature, string TargetPath);

    public string? CurrentSignature { get; private set; }

    public string? RenderedSignature { get; private set; }

    public string? RenderedPath { get; private set; }

    public AudioPreviewMap? Map { get; private set; }

    public string? FailedSignature { get; private set; }

    public Ticket? InFlight { get; private set; }

    public bool IsActive =>
        RenderedPath != null && Map != null && RenderedSignature != null && RenderedSignature == CurrentSignature;

    public bool IsPending =>
        CurrentSignature != null && !IsActive && CurrentSignature != FailedSignature;

    public bool Observe(string? signature, long nowMs)
    {
        if (signature == CurrentSignature) return false;
        CurrentSignature = signature;
        _sigChangedAtMs = nowMs;
        return true;
    }

    public bool ShouldStart(long nowMs, long debounceMs)
    {
        string? sig = CurrentSignature;
        if (sig == null || sig == RenderedSignature || sig == FailedSignature) return false;
        if (InFlight is { } t && t.Signature == sig) return false;
        return nowMs - _sigChangedAtMs >= debounceMs;
    }

    public Ticket Begin(string signature, string targetPath)
    {
        if (RenderedPath != null && string.Equals(RenderedPath, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            RenderedPath = null;
            RenderedSignature = null;
            Map = null;
        }
        var ticket = new Ticket(++_generation, signature, targetPath);
        InFlight = ticket;
        return ticket;
    }

    public bool TryComplete(Ticket ticket, AudioPreviewMap? map, string? signatureNow, bool cancelled)
    {
        bool newest = InFlight is { } t && t.Generation == ticket.Generation;
        if (newest) InFlight = null;
        if (!newest || cancelled || map == null) return false;
        if (signatureNow != ticket.Signature || CurrentSignature != ticket.Signature) return false;

        RenderedPath = ticket.TargetPath;
        RenderedSignature = ticket.Signature;
        Map = map;
        if (FailedSignature == ticket.Signature) FailedSignature = null;
        return true;
    }

    public void Fail(Ticket ticket)
    {
        if (InFlight is not { } t || t.Generation != ticket.Generation) return;
        InFlight = null;
        FailedSignature = ticket.Signature;
    }

    public void Reset()
    {
        InFlight = null;
        RenderedPath = null;
        RenderedSignature = null;
        Map = null;
        FailedSignature = null;
        CurrentSignature = null;
    }
}

public static class PreviewMixPolicy
{
    public static bool NeedsRenderedMix(
        bool hasMusic,
        bool hasVoiceOver,
        bool hasMeme,
        bool tamerWanted,
        double baseSpeed,
        IEnumerable<double>? segmentSpeeds)
    {
        if (hasMusic || hasVoiceOver || hasMeme || tamerWanted) return true;
        if (ChangesTempo(baseSpeed)) return true;
        if (segmentSpeeds != null)
            foreach (double s in segmentSpeeds)
                if (ChangesTempo(s) || IsFreeze(s)) return true;
        return false;
    }

    public static bool IsFreeze(double speed) => Math.Abs(speed) <= 0.001;

    public static bool ChangesTempo(double speed) =>
        Math.Abs(speed) > 0.001 && AudioTempoFilterBuilder.EngineFor(speed, rubberbandAvailable: true) != AudioTempoEngine.None;
}
