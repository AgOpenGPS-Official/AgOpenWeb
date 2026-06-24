// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.Threading;
using System.Threading.Tasks;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Services.Pipeline;

namespace AgOpenWeb.Services.Tests.Pipeline;

[TestFixture]
public class PipelineIntentsTests
{
    [Test]
    public void Drain_returns_empty_when_no_requests()
    {
        var intents = new PipelineIntents();

        var batch = intents.Drain();

        Assert.Multiple(() =>
        {
            Assert.That(batch.ManualYouTurn, Is.Null);
            Assert.That(batch.ClearYouTurn, Is.False);
        });
    }

    [Test]
    public void RequestManualYouTurn_last_wins()
    {
        var intents = new PipelineIntents();

        intents.RequestManualYouTurn(turnLeft: true);
        intents.RequestManualYouTurn(turnLeft: false);
        intents.RequestManualYouTurn(turnLeft: true);

        var batch = intents.Drain();

        Assert.That(batch.ManualYouTurn, Is.True);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void RequestManualYouTurn_encodes_direction_correctly(bool turnLeft)
    {
        var intents = new PipelineIntents();

        intents.RequestManualYouTurn(turnLeft);

        Assert.That(intents.Drain().ManualYouTurn, Is.EqualTo(turnLeft));
    }

    [Test]
    public void Drain_clears_state()
    {
        var intents = new PipelineIntents();
        intents.RequestManualYouTurn(turnLeft: true);
        intents.RequestClearYouTurn();

        var first = intents.Drain();
        var second = intents.Drain();

        Assert.Multiple(() =>
        {
            Assert.That(first.ManualYouTurn, Is.True);
            Assert.That(first.ClearYouTurn, Is.True);
            Assert.That(second.ManualYouTurn, Is.Null);
            Assert.That(second.ClearYouTurn, Is.False);
        });
    }

    [Test]
    public void RequestClearYouTurn_is_idempotent_between_drains()
    {
        var intents = new PipelineIntents();

        intents.RequestClearYouTurn();
        intents.RequestClearYouTurn();
        intents.RequestClearYouTurn();

        Assert.That(intents.Drain().ClearYouTurn, Is.True);
        Assert.That(intents.Drain().ClearYouTurn, Is.False);
    }

    [Test]
    public async Task Concurrent_request_and_drain_never_loses_all_events_or_throws()
    {
        const int writes = 10_000;
        var intents = new PipelineIntents();
        int observedDrains = 0;

        var writer = Task.Run(() =>
        {
            for (int i = 0; i < writes; i++)
            {
                intents.RequestManualYouTurn(turnLeft: (i & 1) == 0);
            }
        });

        var reader = Task.Run(() =>
        {
            while (!writer.IsCompleted)
            {
                var batch = intents.Drain();
                if (batch.ManualYouTurn.HasValue)
                    Interlocked.Increment(ref observedDrains);
            }
        });

        await Task.WhenAll(writer, reader);

        // Flush anything written between the reader's last Drain and the writer finishing.
        var tail = intents.Drain();
        if (tail.ManualYouTurn.HasValue)
            Interlocked.Increment(ref observedDrains);

        Assert.Multiple(() =>
        {
            Assert.That(observedDrains, Is.GreaterThan(0),
                "At least one drain should have observed a request");
            Assert.That(intents.Drain().ManualYouTurn, Is.Null,
                "After writer stops and tail drain completes, no pending request should remain");
        });
    }
}
