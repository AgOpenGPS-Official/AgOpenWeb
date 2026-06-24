using System;
using System.Diagnostics;
using System.Text;
using AgOpenWeb.Services;

namespace TestRunner;

/// <summary>
/// Benchmark for the Span-based, zero-copy NMEA parser.
/// Phase B C5 retired the string-based parser; this harness now just
/// measures throughput and allocation for the surviving fast parser.
/// </summary>
public static class NmeaParserBenchmark
{
    private const string SamplePanda = "$PANDA,123519,4807.038,N,01131.000,E,4,08,0.9,545.4,1.2,5.5,270.5,1.2,-0.5,0.1*4A";

    public static void Run()
    {
        Console.WriteLine("=== NMEA Parser Benchmark ===\n");

        var mockGpsService = new MockGpsService();
        var fastParser = new NmeaParserServiceFast(mockGpsService);
        byte[] sampleBytes = Encoding.ASCII.GetBytes(SamplePanda);

        Console.WriteLine("Warming up...");
        for (int i = 0; i < 1000; i++)
        {
            fastParser.ParseBuffer(sampleBytes, sampleBytes.Length);
        }

        const int iterations = 100_000;
        Console.WriteLine($"\nBenchmarking {iterations:N0} iterations...\n");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long allocsBefore = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < iterations; i++)
        {
            fastParser.ParseBuffer(sampleBytes, sampleBytes.Length);
        }

        sw.Stop();
        long allocsAfter = GC.GetTotalAllocatedBytes(precise: true);
        long fastAllocs = allocsAfter - allocsBefore;
        double fastMs = sw.Elapsed.TotalMilliseconds;
        double fastPerParse = fastMs / iterations * 1000;

        Console.WriteLine("Fast Parser (Span-based, zero-copy):");
        Console.WriteLine($"  Total time:    {fastMs:F2} ms");
        Console.WriteLine($"  Per parse:     {fastPerParse:F3} us");
        Console.WriteLine($"  Allocations:   {fastAllocs:N0} bytes ({fastAllocs / iterations:N0} bytes/parse)");

        Console.WriteLine("\n--- Target Check ---");
        if (fastPerParse < 1000)
        {
            Console.WriteLine($"  OK  Sub-millisecond achieved: {fastPerParse:F3} us < 1000 us");
        }
        else
        {
            Console.WriteLine($"  NG  Sub-millisecond NOT achieved: {fastPerParse:F3} us >= 1000 us");
        }
    }
}

/// <summary>
/// Mock GPS service for benchmarking - does nothing with the data.
/// </summary>
public class MockGpsService : AgOpenWeb.Services.Interfaces.IGpsService
{
    public AgOpenWeb.Models.GpsData CurrentData { get; private set; } = new();

    public bool IsConnected => true;

    public event EventHandler<AgOpenWeb.Models.GpsData>? GpsDataUpdated;

    public void UpdateGpsData(AgOpenWeb.Models.GpsData data)
    {
        CurrentData = data;
        // Don't fire event in benchmark to isolate parser performance
    }

    public void Start() { }
    public void Stop() { }
    public void ProcessNmeaSentence(string sentence) { }
    public void UpdateImuData() { }
    public void MarkGpsReceived() { }
    public void MarkRealGpsParsed() { }
    public bool IsGpsLive => false;
    public bool IsGpsDataOk() => true;
    public bool IsImuDataOk() => true;
}
