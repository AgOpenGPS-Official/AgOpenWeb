// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace AgOpenWeb.Services;

/// <summary>One step of an NTRIP session, kept for the bug report.</summary>
/// <param name="Service">The service it was for, as <see cref="NtripServiceInfo.ToString"/> shows it.</param>
/// <param name="Attempts">Failed attempts since corrections last arrived, when the step happened.</param>
/// <param name="FirstLine">The caster's first reply line.</param>
public sealed record NtripEvent(DateTime Time, string Service, NtripState State, NtripCategory? Category,
    string Message, string FirstLine, NtripVersion Version, int Attempts);

/// <summary>
/// Which service the client is set to. It says whether a login is set, never what it is. The host and
/// the mount point are shown as set, even where a user name happens to be the same text.
/// </summary>
public sealed record NtripServiceInfo(string Host, int Port, string Mount, bool HasUserName, bool HasPassword)
{
    public override string ToString() => $"{Mount}@{Host}:{Port.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>The last steps of the sessions, oldest first. A step that repeats the last one is not written again. Thread-safe.</summary>
public sealed class NtripEventLog
{
    public const int MaxEvents = 40;

    private readonly List<NtripEvent> _events = [];

    public void Add(NtripEvent e)
    {
        lock (_events)
        {
            if (_events.Count > 0)
            {
                NtripEvent last = _events[^1];
                if (last.State == e.State && last.Category == e.Category && last.Message == e.Message && last.Attempts == e.Attempts) return;
            }
            _events.Add(e);
            if (_events.Count > MaxEvents) _events.RemoveAt(0);
        }
    }

    public IReadOnlyList<NtripEvent> ToArray()
    {
        lock (_events) return _events.ToArray();
    }
}
