using System;
using System.Xml;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Services.IsoXml;

namespace AgOpenWeb.Services.Tests;

[TestFixture]
public class IsoXmlParserHelpersTests
{
    private const string SamplePointsThree =
        "<PNT C='0.0' D='0.0'/><PNT C='0.001' D='0.0'/><PNT C='0.001' D='0.001'/>";

    private const string SamplePointsFour =
        "<PNT C='0.0' D='0.0'/><PNT C='0.002' D='0.0'/><PNT C='0.002' D='0.002'/><PNT C='0.0' D='0.002'/>";

    private static LocalPlane MakePlane()
        => new LocalPlane(new Wgs84(0.0, 0.0), new SharedFieldProperties());

    private static XmlNodeList MakeFieldParts(string innerXml)
    {
        var doc = new XmlDocument();
        doc.LoadXml($"<PFD>{innerXml}</PFD>");
        return doc.DocumentElement!.ChildNodes;
    }

    [Test]
    public void ParseBoundaries_InnerHole_LsgA1_Parses()
    {
        var xml = $@"
            <PLN A='1'><LSG A='1'>{SamplePointsThree}</LSG></PLN>
            <PLN A='3'><LSG A='1'>{SamplePointsThree}</LSG></PLN>";

        var boundaries = IsoXmlParserHelpers.ParseBoundaries(MakeFieldParts(xml), MakePlane());

        Assert.That(boundaries, Has.Count.EqualTo(2));
    }

    [Test]
    public void ParseBoundaries_InnerHole_LsgA2_NowParses()
    {
        var xml = $@"
            <PLN A='1'><LSG A='1'>{SamplePointsThree}</LSG></PLN>
            <PLN A='3'><LSG A='2'>{SamplePointsThree}</LSG></PLN>";

        var boundaries = IsoXmlParserHelpers.ParseBoundaries(MakeFieldParts(xml), MakePlane());

        Assert.That(boundaries, Has.Count.EqualTo(2),
            "Inner boundary using LSG type 2 (interior) should be parsed");
    }

    [Test]
    public void ParseBoundaries_InnerHole_BothLsgA1AndA2_PrefersA1()
    {
        // PLN A='3' carries both LSG types. The `??` fallback picks A='1' first.
        // Distinguish by point count: A='1' has 3 points, A='2' has 4.
        var xml = $@"
            <PLN A='1'><LSG A='1'>{SamplePointsThree}</LSG></PLN>
            <PLN A='3'>
                <LSG A='1'>{SamplePointsThree}</LSG>
                <LSG A='2'>{SamplePointsFour}</LSG>
            </PLN>";

        var boundaries = IsoXmlParserHelpers.ParseBoundaries(MakeFieldParts(xml), MakePlane());

        Assert.That(boundaries, Has.Count.EqualTo(2));
        Assert.That(boundaries[1].FenceLine, Has.Count.EqualTo(3),
            "Inner boundary should reflect the A='1' shape (3 points), not A='2' (4 points)");
    }

    [Test]
    public void ParseBoundaries_KeepsEastingAndNorthingOnTheRightAxes()
    {
        // Regression guard: the parser used to transpose Easting/Northing, so an
        // imported field came out mirrored across the 45° line. With the origin at
        // (0,0): point 1 is due EAST (lon +0.001, lat 0) and point 2 adds NORTH.
        var xml = "<PLN A='1'><LSG A='1'>"
                + "<PNT C='0.0' D='0.0'/>"      // origin
                + "<PNT C='0.0' D='0.001'/>"    // due east
                + "<PNT C='0.001' D='0.001'/>"  // east + north
                + "</LSG></PLN>";

        var fence = IsoXmlParserHelpers.ParseBoundaries(MakeFieldParts(xml), MakePlane())[0].FenceLine;

        // Due-east point: large Easting, ~zero Northing (would be swapped if transposed).
        Assert.That(fence[1].Easting, Is.GreaterThan(50), "due-east point should have a large Easting");
        Assert.That(Math.Abs(fence[1].Northing), Is.LessThan(5), "due-east point should have ~zero Northing");

        // Adding latitude must move Northing, not Easting.
        Assert.That(fence[2].Northing, Is.GreaterThan(50), "adding north should increase Northing");
    }
}
