using System.Globalization;
using System.Reflection;
using System.Threading;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgOpenWeb.Services.Tests;

/// <summary>The GGA sent to VRS casters must use '.' decimals whatever the device locale.</summary>
[TestFixture]
[NonParallelizable]
public class NtripGgaCultureTests
{
    [TestCase("de-DE")]
    [TestCase("el-GR")]
    [TestCase("en-US")]
    public void Gga_IsLocaleIndependent(string culture)
    {
        var svc = new NtripClientService(NSubstitute.Substitute.For<IGpsService>(),
            NullLogger<NtripClientService>.Instance);
        var gen = typeof(NtripClientService).GetMethod("GenerateGgaSentence",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        var saved = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo(culture);
            var gga = (string)gen.Invoke(svc, new object[] { 52.5, -5.25, 12.3, 4, 12 })!;
            var f = gga.Split('*')[0].Split(',');
            Assert.That(f[2], Is.EqualTo("5230.0000"));
            Assert.That(f[3], Is.EqualTo("N"));
            Assert.That(f[4], Is.EqualTo("00515.0000"));
            Assert.That(f[5], Is.EqualTo("W"));
            Assert.That(f[9], Is.EqualTo("12.3"));
            Assert.That(f, Has.Length.EqualTo(15), gga);
        }
        finally { Thread.CurrentThread.CurrentCulture = saved; }
    }
}
