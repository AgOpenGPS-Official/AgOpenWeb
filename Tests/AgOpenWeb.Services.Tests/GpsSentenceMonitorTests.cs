using System.Diagnostics;
using System.Linq;
using System.Text;
using AgOpenWeb.Services.Gps;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests;

[TestFixture]
public class GpsSentenceMonitorTests
{
    private const string Panda = "$PANDA,162255.50,3924.90,N,00731.80,W,4,12,0.7,341.9,1.2,4.8,2217,31,-12,0.5*5A\r\n";
    private const string Paogi = "$PAOGI,162255.50,3924.90,N,00731.80,W,4,12,0.7,341.9,1.2,4.8,221.7,3.1,-1.2,0.5*4B\r\n";

    private const string Ksxt = "$KSXT,20190909084745.00,116.23662400,40.07897925,68.3837,2.5,-1.1,0.00,0.00,0.00,0.00,3,3,27,27,0.000,0.000,0.000,0.000,0.000,1.200,0.000*00\r\n";

    private static long At(double seconds) => (long)(seconds * Stopwatch.Frequency);
    private static byte[] B(string s) => Encoding.ASCII.GetBytes(s);

    [Test]
    public void Keeps_the_latest_text_of_each_sentence_type_with_its_age()
    {
        var m = new GpsSentenceMonitor();
        m.Record(B(Panda), accepted: true, At(10.0));
        m.Record(B(Paogi), accepted: true, At(10.1));

        var snap = m.GetSnapshot(At(10.6));

        Assert.That(snap.Sentences.Select(s => s.Type), Is.EqualTo(new[] { "PANDA", "PAOGI" }));
        Assert.That(snap.Sentences[0].Text, Is.EqualTo(Panda.TrimEnd()), "Line ends are dropped");
        Assert.That(snap.Sentences[0].AgeSeconds, Is.EqualTo(0.6).Within(0.001));
        Assert.That(snap.Sentences[1].AgeSeconds, Is.EqualTo(0.5).Within(0.001));
    }

    [Test]
    public void A_KSXT_sentence_gets_its_own_slot()
    {
        var m = new GpsSentenceMonitor();
        m.Record(B(Panda), accepted: true, At(10.0));
        m.Record(B(Ksxt), accepted: true, At(10.1));

        var snap = m.GetSnapshot(At(10.2));

        Assert.That(snap.Sentences.Select(s => s.Type), Is.EqualTo(new[] { "PANDA", "KSXT" }));
        Assert.That(snap.Sentences[1].Text, Is.EqualTo(Ksxt.TrimEnd()));
    }

    [Test]
    public void Nothing_received_gives_an_empty_snapshot()
    {
        var snap = new GpsSentenceMonitor().GetSnapshot(At(1));

        Assert.That(snap.Sentences, Is.Empty);
        Assert.That(snap.RateHz, Is.Zero);
        Assert.That(snap.Missed, Is.Zero);
    }

    [Test]
    public void Rate_is_measured_over_recent_arrivals_and_reads_zero_when_silent()
    {
        var m = new GpsSentenceMonitor();
        for (int i = 0; i < 30; i++) m.Record(B(Panda), true, At(10 + i * 0.1));

        Assert.That(m.GetSnapshot(At(12.95)).RateHz, Is.EqualTo(10.0).Within(0.01));
        Assert.That(m.GetSnapshot(At(16.0)).RateHz, Is.Zero, "No sentence for over 2 s");
    }

    [Test]
    public void A_gap_counts_the_sentences_that_did_not_come()
    {
        var m = new GpsSentenceMonitor();
        double t = 10;
        for (int i = 0; i < 20; i++, t += 0.1) m.Record(B(Panda), true, At(t));
        t += 0.3; // three sentences missing: the next one is 0.4 s after the last
        m.Record(B(Panda), true, At(t));
        for (int i = 0; i < 5; i++) { t += 0.1; m.Record(B(Panda), true, At(t)); }

        Assert.That(m.GetSnapshot(At(t)).Missed, Is.EqualTo(3));
    }

    [Test]
    public void A_long_silence_is_a_new_session_not_missed_sentences()
    {
        var m = new GpsSentenceMonitor();
        for (int i = 0; i < 20; i++) m.Record(B(Panda), true, At(10 + i * 0.1));
        for (int i = 0; i < 20; i++) m.Record(B(Panda), true, At(60 + i * 0.1));

        var snap = m.GetSnapshot(At(61.9));
        Assert.That(snap.Missed, Is.Zero);
        Assert.That(snap.RateHz, Is.EqualTo(10.0).Within(0.01), "The rate window restarts with the session");
    }

    [Test]
    public void A_refused_datagram_is_counted_and_kept_apart_from_the_rate()
    {
        var m = new GpsSentenceMonitor();
        for (int i = 0; i < 10; i++) m.Record(B(Panda), true, At(10 + i * 0.1));
        m.Record(new byte[] { (byte)'$', (byte)'G', 0x00, 0xFF, (byte)'A', (byte)'\n' }, accepted: false, At(10.95));

        var snap = m.GetSnapshot(At(11.0));

        Assert.That(snap.Rejected, Is.EqualTo(1));
        Assert.That(snap.Missed, Is.Zero);
        var rejected = snap.Sentences.Single(s => s.Type == GpsSentenceMonitor.RejectedType);
        Assert.That(rejected.Text, Is.EqualTo("$G??A"), "Only printable ASCII reaches the browser");
        Assert.That(snap.RateHz, Is.EqualTo(10.0).Within(0.01));
    }

    [Test]
    public void An_overlong_datagram_is_cut_not_dropped()
    {
        var m = new GpsSentenceMonitor();
        m.Record(B("$PANDA," + new string('7', 500)), true, At(1));

        Assert.That(m.GetSnapshot(At(1)).Sentences[0].Text, Has.Length.EqualTo(200));
    }
}
