// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;

namespace FreeVideoStudio.Core.Media;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// PREVIEWMIX_02 — WHICH RENDERED MIX MAY BE HEARD. One owner for the rendered preview mix's state,
/// so "an older render can never replace newer settings" is a property of one small, tested type
/// instead of five fields scattered across a window.
///
/// The rules (each one closed a real hole in the first version):
/// <list type="number">
/// <item><b>Invalidate on observation, not on a timer.</b> <see cref="Observe"/> is called with the
/// CURRENT signature before any mix audio is played; a changed signature makes <see cref="IsActive"/>
/// false at once, so the live players take over before a single stale sample is heard.</item>
/// <item><b>Every render is a numbered ticket.</b> Only the NEWEST ticket may complete, and only if the
/// signature it rendered is STILL the current one, re-computed at completion
/// (<see cref="TryComplete"/>). A render that finishes after an edit, or after a newer render
/// started, is discarded.</item>
/// <item><b>A WAV that is being overwritten is no longer a valid mix.</b> The renders alternate between
/// two files. Starting a render into the file the current mix lives in invalidates that mix first;
/// before, undoing back to its settings re-activated a file that held a half-written newer render.</item>
/// <item><b>A failed render is not retried in a loop.</b> The failed signature is latched
/// (<see cref="FailedSignature"/>) and only a NEW edit renders again — the first version retried every
/// 250 ms for as long as the failure lasted.</item>
/// </list>
/// Not thread-safe by design: every call is made on the UI thread.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public sealed class RenderedMixGate
{
    private long _generation;
    private long _sigChangedAtMs;

    /// <summary>A render in flight: its number, the signature it renders and the file it writes.</summary>
    public readonly record struct Ticket(long Generation, string Signature, string TargetPath);

    /// <summary>The signature of the edit as it is NOW (null = the edit needs no rendered mix).</summary>
    public string? CurrentSignature { get; private set; }

    /// <summary>The signature the valid rendered mix was made from, or null.</summary>
    public string? RenderedSignature { get; private set; }

    /// <summary>The WAV of the valid rendered mix, or null.</summary>
    public string? RenderedPath { get; private set; }

    /// <summary>How preview time maps into <see cref="RenderedPath"/>.</summary>
    public AudioPreviewMap? Map { get; private set; }

    /// <summary>The signature whose render last FAILED (latched until the edit changes).</summary>
    public string? FailedSignature { get; private set; }

    /// <summary>The ticket currently allowed to complete, if any.</summary>
    public Ticket? InFlight { get; private set; }

    /// <summary>True while the rendered mix matches the current edit and replaces the live players.</summary>
    public bool IsActive =>
        RenderedPath != null && Map != null && RenderedSignature != null && RenderedSignature == CurrentSignature;

    /// <summary>True when the edit needs a mix that is neither ready, nor rendering, nor known to fail.</summary>
    public bool IsPending =>
        CurrentSignature != null && !IsActive && CurrentSignature != FailedSignature;

    /// <summary>
    /// Records the current signature. Returns true when it changed (the mix, if any, is no longer active
    /// from this call on). <paramref name="nowMs"/> starts the debounce for <see cref="ShouldStart"/>.
    /// </summary>
    public bool Observe(string? signature, long nowMs)
    {
        if (signature == CurrentSignature) return false;
        CurrentSignature = signature;
        _sigChangedAtMs = nowMs;
        return true;
    }

    /// <summary>True when a render of the current signature should start now (debounced).</summary>
    public bool ShouldStart(long nowMs, long debounceMs)
    {
        string? sig = CurrentSignature;
        if (sig == null || sig == RenderedSignature || sig == FailedSignature) return false;
        if (InFlight is { } t && t.Signature == sig) return false;
        return nowMs - _sigChangedAtMs >= debounceMs;
    }

    /// <summary>
    /// Starts a render of the current signature into <paramref name="targetPath"/>. Any older ticket is
    /// superseded. If the target is the file the valid mix lives in, that mix is invalidated first.
    /// </summary>
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

    /// <summary>
    /// Accepts a finished render only if it is the newest ticket and its signature still equals
    /// <paramref name="signatureNow"/> (re-computed by the caller at completion). Returns true when the
    /// mix became the valid one.
    /// </summary>
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

    /// <summary>Records a render that failed (not cancelled): its signature is not retried until the edit changes.</summary>
    public void Fail(Ticket ticket)
    {
        if (InFlight is not { } t || t.Generation != ticket.Generation) return;   // superseded: not this edit's verdict
        InFlight = null;
        FailedSignature = ticket.Signature;
    }

    /// <summary>Drops everything (the clip was unloaded or the window is closing).</summary>
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

/// <summary>
/// PREVIEWMIX_02 — when the edit needs the export's rendered audio instead of the live players. The live
/// players cannot reproduce: the music's sidechain ducking/carving and edge fades, the voice protection's
/// carve, the safety limiter, the meme levels, the peak tamer measured over the exported range, and the
/// export's tempo engine (TEMPO_01: atempo / Rubber Band — the live gameplay player time-stretches with
/// mpv's own scaletempo). Any of them present means a rendered mix.
/// </summary>
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
                if (ChangesTempo(s)) return true;
        return false;
    }

    /// <summary>
    /// True when the export runs a tempo filter for this rate (TEMPO_01). A freeze (0x) has no tempo: its
    /// audio is synthesised silence.
    /// </summary>
    public static bool ChangesTempo(double speed) =>
        Math.Abs(speed) > 0.001 && AudioTempoFilterBuilder.EngineFor(speed, rubberbandAvailable: true) != AudioTempoEngine.None;
}
