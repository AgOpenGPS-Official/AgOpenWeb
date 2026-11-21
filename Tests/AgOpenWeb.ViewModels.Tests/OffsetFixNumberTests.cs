// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Configuration;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Offset Fix › typed offset (<c>offset.set|E,N</c>): a value that is no number is not taken. "NaN"
/// and "Infinity" parse as numbers, and an offset of NaN put every position the GPS gives at NaN.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class OffsetFixNumberTests
{
    [SetUp]
    public void SetUp() => ConfigurationStore.SetInstance(new ConfigurationStore());

    [TearDown]
    public void TearDown() => ConfigurationStore.SetInstance(new ConfigurationStore());

    [TestCase(double.NaN, 0.0)]
    [TestCase(0.0, double.NaN)]
    [TestCase(double.PositiveInfinity, 0.0)]
    [TestCase(0.0, double.NegativeInfinity)]
    public void An_offset_that_is_no_number_is_not_taken(double easting, double northing)
    {
        var vm = new MainViewModelBuilder().Build();
        vm.OffsetFixSet(1.5, -0.5);

        vm.OffsetFixSet(easting, northing);

        Assert.Multiple(() =>
        {
            Assert.That(vm.State.Field.DriftEasting, Is.EqualTo(1.5));
            Assert.That(vm.State.Field.DriftNorthing, Is.EqualTo(-0.5));
        });
    }
}
