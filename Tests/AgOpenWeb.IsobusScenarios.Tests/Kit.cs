// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace AgOpenWeb.IsobusScenarios.Tests;

/// <summary>What the scenarios share: data folders, the implement check, the wire check.</summary>
public static class Kit
{
    public static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    public static bool Want(string? only, string code) =>
        only == null || only.Split(',').Any(o => code.StartsWith(o.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string NewDataRoot(string code) =>
        Path.Combine(Path.GetTempPath(), "aow-isobus-" + code + "-" + Guid.NewGuid().ToString("N")[..8]);

    public const string NoSectionsUnder17 =
        "AgOpenWeb sends PGN 229 (the 64 section states) to the modules only when the tool has more than 16 sections (AutoSteerService.SendPgns: \"below 17 sections, 239 is sufficient and 229 is skipped\"), but the task controller reads the sections from PGN 229 only (AOG-TaskController app.cpp handles 0xE5, 0xF1, 0xF2 and 0xD6; not 254 or 239). So an ISOBUS implement with 16 sections or fewer never gets AgOpenWeb's section states: in automatic section control it does nothing while the screen paints coverage. AgOpenGPS sends 229 whatever the section count (AgIO relays it to the modules), and so does AgOpenWeb's own loopback copy for rate controllers (SendLoopbackPgns), which the task controller does not listen to.";

    /// <summary>Drive and compare the screen with the implement; record what disagreed and why.
    /// <paramref name="expectFollow"/>: the implement should follow the screen (automatic
    /// section control on, task controller up).</summary>
    public static async Task<IsobusSession.Watch> DriveAsync(Scenario sc, IsobusSession s, double seconds, string what,
        Implement? implement = null, bool expectFollow = true)
    {
        var w = await s.WatchAsync(seconds, implement);
        int n = s.Store.NumSections;
        sc.Step(what + ": " + w.Summary(n));
        if (w.FrameDisagreeSeconds > 0.5)
            sc.Problem(Severity.Inconsistency, "ISOBUS-FRAME-DISAGREES", F($"{what}: the status frame (the ISOBUS button) and the service disagreed on whether a task controller is heard for {w.FrameDisagreeSeconds:0.0} s"));
        if (!expectFollow) return w;

        var impl = implement ?? s.Tc.Implements.FirstOrDefault();
        int implSections = impl?.Sections ?? 0;
        if (w.ScreenOnSeconds >= 1 && w.ScreenOnNoSetpointsSeconds >= w.ScreenOnSeconds - 0.3 && s.Tc.Count(229) == 0)
        {
            sc.Problem(Severity.Bug, "ISOBUS-NO-SECTIONS-UNDER-17",
                F($"{what}: the screen had sections on for {w.ScreenOnSeconds:0.0} s and painted {w.AreaPainted:0} m², the task controller received PGN 254 ×{s.Tc.Count(254)} and 239 ×{s.Tc.Count(239)} but no PGN 229 at all, so the {implSections}-section implement did nothing (its sections stayed {Bits(impl?.Actual ?? 0, implSections)}). ") + NoSectionsUnder17);
            return w;
        }
        var lag = new List<string>();
        for (int i = 0; i < Math.Min(n, implSections); i++)
        {
            double limit = (impl?.ValveDelayMs ?? 300) / 1000.0 + 0.7;
            if (w.OnButImplementOff[i] < limit && w.OffButImplementOn[i] < limit) continue;
            lag.Add(F($"{i + 1} (on screen, implement off {w.OnButImplementOff[i]:0.0} s; off on screen, implement on {w.OffButImplementOn[i]:0.0} s)"));
        }
        if (lag.Count == 0)
            sc.Ok(what + ": every implement section followed the screen" + (implSections > 0 && n != implSections ? F($" (the first {Math.Min(n, implSections)}: tool {n} sections, implement {implSections})") : ""));
        else
            sc.Problem(Severity.Bug, "ISOBUS-SECTIONS-NOT-FOLLOWING", what + ": with the task controller up and section control automatic, the implement did not follow the screen for section(s) " + string.Join(", ", lag.Take(8)) + (lag.Count > 8 ? F($" … ({lag.Count})") : ""));
        if (w.ReportedDifferSeconds > 0.5)
            sc.Problem(Severity.Bug, "ISOBUS-REPORTED-STALE", F($"{what}: the sections AgOpenWeb reports for the implement (the ISOBUS row, from PGN 240) differed from the implement's own for {w.ReportedDifferSeconds:0.0} s of {w.Seconds:0.0} s (the heartbeat comes every 0.1 s)"));
        else if (implSections > 0 && w.AliveSeconds > 1)
            sc.Ok(what + ": the ISOBUS row showed the implement's own sections (PGN 240) within 0.5 s");
        if (w.ModeDifferSeconds > 0.5)
            sc.Problem(Severity.Bug, "ISOBUS-MODE-SHOWN-WRONG", F($"{what}: the mode on the ISOBUS button differed from the task controller's for {w.ModeDifferSeconds:0.0} s"));
        return w;
    }

    public static string Bits(ulong bits, int n)
    {
        if (n <= 0) return "none";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < Math.Min(n, 64); i++) sb.Append(((bits >> i) & 1) != 0 ? '1' : '0');
        return sb.ToString();
    }

    /// <summary>The run's protocol health: no bad checksums, no cut packets.</summary>
    public static void CheckWire(Scenario sc, EmulatedTaskController tc)
    {
        if (tc.ChecksumErrors > 0 || tc.ShortPackets > 0)
            sc.Problem(Severity.Bug, "PGN-BAD-PACKETS", F($"the task controller received {tc.ChecksumErrors} packet(s) with a wrong checksum and {tc.ShortPackets} cut short (it does not check them, AgOpenGPS modules do)"));
    }

    /// <summary>The hardware messages (PGN 221) the task controller sent since <paramref name="sinceMessages"/>
    /// against the ones AgOpenWeb parsed since <paramref name="sinceSeconds"/>.</summary>
    public static void CheckMessages(Scenario sc, IsobusSession s, int sinceMessages, double sinceSeconds, string when)
    {
        var sent = s.Tc.MessagesSent.Skip(sinceMessages).ToList();
        List<(double At, string Text, int Seconds, bool Warning)> got;
        lock (s.HardwareMessages) got = s.HardwareMessages.Where(m => m.At >= sinceSeconds).ToList();
        if (sent.Count == 0) return;
        var missing = sent.Where(m => !got.Any(g => g.Text == m.Text)).Select(m => "\"" + m.Text + "\"").ToList();
        bool shown = s.Store.Display.HardwareMessagesEnabled;
        if (missing.Count == 0)
            sc.Ok(F($"{when}: the task controller's message(s) {string.Join(", ", sent.Select(m => "\"" + m.Text + "\""))} reached AgOpenWeb (PGN 221 from source 0x80), and hardware messages are {(shown ? "on" : "OFF")} in Display, so the operator {(shown ? "sees them" : "does not see them")}"));
        else
            sc.Problem(Severity.Bug, "ISOBUS-MESSAGE-LOST", F($"{when}: the task controller's message(s) {string.Join(", ", missing)} did not reach AgOpenWeb's hardware message handler (PGN 221, source 0x80)"));
        if (!shown)
            sc.Note("hardware messages are off in Display settings here; the default is on");
        var wrongLevel = sent.Where(m => got.Any(g => g.Text == m.Text && g.Warning != m.Alert)).ToList();
        if (wrongLevel.Count > 0)
            sc.Problem(Severity.Bug, "ISOBUS-MESSAGE-LEVEL", F($"{when}: {wrongLevel.Count} message(s) arrived with the other level (the task controller sends colour 0 for an alert, 1 for information; AgOpenWeb reads colour 0 as a warning)"));
    }

    /// <summary>What AgOpenWeb told the operator meanwhile (failures, warnings, status lines).</summary>
    public static List<string> Said(IsobusSession s, Func<string, bool>? about = null)
    {
        return s.Rig.TakeEvents()
            .Where(e => e.Kind is RigEventKind.Failure or RigEventKind.Status or RigEventKind.Warning or RigEventKind.Error)
            .Select(e => e.Kind + ": " + e.Text.Split('\n')[0])
            .Where(t => about == null || about(t))
            .Distinct()
            .Take(8)
            .ToList();
    }

    /// <summary>Exceptions, hung loop, VM changed off the host loop: the harness's own red flags.</summary>
    public static void CheckRig(Scenario sc, IsobusSession s, string when)
    {
        var bad = s.Rig.TakeEvents().Where(e => e.Kind is RigEventKind.Exception or RigEventKind.Hang or RigEventKind.OffLoop or RigEventKind.Error).ToList();
        if (bad.Count == 0) { sc.Ok(when + ": no exception, no hung host loop, no error logged"); return; }
        foreach (var e in bad.Take(4))
            sc.Problem(e.Kind == RigEventKind.Hang ? Severity.Safety : Severity.Bug, "ISOBUS-" + e.Kind.ToString().ToUpperInvariant(), when + ": " + e.Text.Split('\n')[0]);
    }
}
