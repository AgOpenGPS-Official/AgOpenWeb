// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.Collections.Generic;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Services.YouTurn;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>#111: Alternative skip mode reproduces AgOpenGPS's pass order
/// (CYouTurn.YouTurnTrigger / Set_Alternate_skips).</summary>
[TestFixture]
public class AlternateSkipPatternTests
{
    // Passes visited from 0, as HandleAlternateCreation + CompleteTurn step them.
    private static List<int> Passes(int baseWidth, int turns)
    {
        var t = new YouTurnWorkingState
        {
            AltSign = 1, AltBaseWidth = baseWidth, AltWidth = baseWidth,
            AltTurnSkips = baseWidth * 2 - 1, AltPrevBig = false,
        };
        var list = new List<int> { 0 };
        int pass = 0;
        for (int i = 0; i < turns; i++)
        {
            pass += t.AltSign * t.AltWidth;
            list.Add(pass);
            YouTurnStateMachine.AdvanceAlternate(t);
        }
        return list;
    }

    [Test]
    public void SkipTwoRows_CoversEveryPass_OutAndBack()
    {
        // AgOpenGPS with skip 2 (W = 3): 0,3,1,4,2,5 then 8,6,9,7,10 …
        Assert.That(Passes(3, 10), Is.EqualTo(new[] { 0, 3, 1, 4, 2, 5, 8, 6, 9, 7, 10 }));
    }

    [Test]
    public void SkipOneRow_Pattern()
    {
        // W = 2: 0,2,1,3 then 5,4,6 …
        Assert.That(Passes(2, 6), Is.EqualTo(new[] { 0, 2, 1, 3, 5, 4, 6 }));
    }
}
