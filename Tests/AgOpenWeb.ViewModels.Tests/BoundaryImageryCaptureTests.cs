using System;
using System.IO;
using System.Threading.Tasks;
using AgOpenWeb.RemoteServer;
using NUnit.Framework;

namespace AgOpenWeb.ViewModels.Tests;

[TestFixture]
public class BoundaryImageryCaptureTests
{
    [Test]
    public async Task CaptureAsync_ZeroOrNegativeExtents_ReturnsNull()
    {
        var result = await BoundaryImageryCapture.CaptureAsync(100, 50, 100, 50);
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task CaptureAsync_EqualMinMax_ReturnsNull()
    {
        var result = await BoundaryImageryCapture.CaptureAsync(100, 100, 200, 200);
        Assert.That(result, Is.Null);
    }
}
