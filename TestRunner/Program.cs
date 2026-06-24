using AgOpenWeb.Services.Track;
using TestRunner;

// Check for command line args
if (args.Length > 0 && args[0] == "benchmark")
{
    NmeaParserBenchmark.Run();
    return;
}

Console.WriteLine("Running TrackGuidanceService Tests...\n");

var (success, results) = TrackGuidanceServiceTests.RunAllTests();

Console.WriteLine(results);

Environment.Exit(success ? 0 : 1);
