// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using AgOpenWeb.Services.Logging;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.Logging;

/// <summary>
/// Every entry of the app's log is one line of a bug report's logs.txt. A message can hold text from
/// outside the app: what a page says of itself (<c>diag.page</c>) and the reason it gives when it
/// closes its link, both from any browser, an observer too; a track or NTRIP profile named on a
/// screen; a gateway's name on the network. A line break in it wrote a line of its own that read as
/// the app's.
/// </summary>
[TestFixture]
[NonParallelizable] // LogStore is one for the process
public class AppLogOneLineTests
{
    [Test]
    public void A_line_break_in_a_page_note_stays_on_its_line()
    {
        string marker = Guid.NewGuid().ToString("N");
        var log = new InMemoryLoggerProvider().CreateLogger("Browser");

        // As WebBackend writes the hub's lines: "[Browser] <name> page: <the page's note>".
        log.LogInformation("{Line}", $"[Browser] {marker} page: hidden\r\n[Steer settings] Ackermann 90 -> 0");

        var entry = LogStore.Instance.GetSnapshot().Single(e => e.Message.Contains(marker));
        Assert.That(entry.Message, Is.EqualTo($"[Browser] {marker} page: hidden\\r\\n[Steer settings] Ackermann 90 -> 0"));
    }

    [Test]
    public void A_line_break_in_a_logged_name_stays_on_its_line()
    {
        string marker = Guid.NewGuid().ToString("N");
        var log = new InMemoryLoggerProvider().CreateLogger("Test");

        log.LogInformation("Connecting default NTRIP profile '{ProfileName}'", marker + "\n[Control] made up");

        var entry = LogStore.Instance.GetSnapshot().Single(e => e.Message.Contains(marker));
        Assert.That(entry.Message, Is.EqualTo($"Connecting default NTRIP profile '{marker}\\n[Control] made up'"));
    }

    [Test]
    public void An_exception_keeps_its_own_lines_below_the_message()
    {
        string marker = Guid.NewGuid().ToString("N");
        var log = new InMemoryLoggerProvider().CreateLogger("Test");

        log.LogWarning(new InvalidOperationException("broken\nhere"), "[Field] Could not load field {Field}", marker);

        var entry = LogStore.Instance.GetSnapshot().Single(e => e.Message.Contains(marker));
        var lines = entry.Message.Split(Environment.NewLine);
        Assert.Multiple(() =>
        {
            Assert.That(lines[0], Is.EqualTo($"[Field] Could not load field {marker}"));
            Assert.That(entry.Message, Does.Contain("broken\nhere"), "the exception as it is");
        });
    }
}
