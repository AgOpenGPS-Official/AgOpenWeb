// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Profile;

namespace AgOpenWeb.Services.Tests;

/// <summary>PGN 247, vehicle geometry (#387): the byte layout Docs/PGN.md describes.</summary>
[TestFixture]
public class VehicleGeometryPgnTests
{
    private static int U16(byte[] b, int at) => b[at] | (b[at + 1] << 8);
    private static int I16(byte[] b, int at) => (short)(b[at] | (b[at + 1] << 8));

    [Test]
    public void Layout()
    {
        var v = new VehicleConfig
        {
            Wheelbase = 3.2, TrackWidth = 1.85, AntennaHeight = 3.05, AntennaPivot = 0.6,
            AntennaOffset = -0.25, ImuPivot = -0.4, ImuHeight = 0.9, ImuOffset = 0.12, MaxSteerAngle = 38,
        };

        var b = PgnBuilder.BuildVehicleGeometryPgn(v);

        Assert.That(b, Has.Length.EqualTo(25));
        Assert.That(new[] { b[0], b[1], b[2], b[3], b[4] }, Is.EqualTo(new byte[] { 0x80, 0x81, 0x7F, 0xF7, 19 }));
        Assert.That(b[5], Is.EqualTo(1), "layout version");
        Assert.That(b[6], Is.EqualTo(1), "IMU position entered");
        Assert.That(U16(b, 7), Is.EqualTo(320));
        Assert.That(U16(b, 9), Is.EqualTo(185));
        Assert.That(U16(b, 11), Is.EqualTo(305));
        Assert.That(I16(b, 13), Is.EqualTo(60));
        Assert.That(I16(b, 15), Is.EqualTo(-25));
        Assert.That(I16(b, 17), Is.EqualTo(-40));
        Assert.That(U16(b, 19), Is.EqualTo(90));
        Assert.That(I16(b, 21), Is.EqualTo(12));
        Assert.That(b[23], Is.EqualTo(38));
        Assert.That(PgnBuilder.ValidateChecksum(b), Is.True);
    }

    [Test]
    public void NoImuPosition_ClearsTheFlag()
    {
        var b = PgnBuilder.BuildVehicleGeometryPgn(new VehicleConfig());
        Assert.That(b[6], Is.EqualTo(0));
        Assert.That(new[] { b[17], b[18], b[19], b[20], b[21], b[22] }, Is.All.EqualTo(0));
    }

    [Test]
    public void ImuPosition_IsSavedWithTheVehicleProfile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aow-imu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new ConfigurationStore();
            store.Vehicle.ImuPivot = -0.4; store.Vehicle.ImuHeight = 0.9; store.Vehicle.ImuOffset = 0.12;
            VehicleProfileJsonService.Save(dir, "Rig", store);

            var reload = new ConfigurationStore();
            Assert.That(VehicleProfileJsonService.Load(dir, "Rig", reload), Is.True);
            Assert.That(reload.Vehicle.ImuPivot, Is.EqualTo(-0.4).Within(1e-9));
            Assert.That(reload.Vehicle.ImuHeight, Is.EqualTo(0.9).Within(1e-9));
            Assert.That(reload.Vehicle.ImuOffset, Is.EqualTo(0.12).Within(1e-9));
        }
        finally { Directory.Delete(dir, true); }
    }
}
