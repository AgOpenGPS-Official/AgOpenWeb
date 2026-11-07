// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.FieldJobScenarios.Tests;

/// <summary>
/// The careful operator: the machine typed in right, a headland twice the implement wide,
/// section control in the headland, "Off outside boundary" on, U-turns on, sections Auto,
/// AutoSteer engaged at the first pass, and then hands off until AgOpenWeb says the field is done.
/// </summary>
public static class CarefulOperator
{
    public static readonly SystemSpec Puma = new("um982", "tm171", "keya");

    public static IEnumerable<JobSpec> Jobs()
    {
        yield return new JobSpec("J1", "Olival, 8 m mounted sprayer, outer boundary only",
            "The smallest field (3.75 ha), the Case Puma with the 8 m sprayer in 8 sections, AB line at 159°, headland 16 m.",
            "Olival", "AB 159 deg", Machines.Puma, Machines.Sprayer8, Puma);
        yield return new JobSpec("J2", "Olival with its 13 obstacles, 8 m sprayer",
            "The same field with its inner boundaries: AgOpenWeb drives straight on (obstacle turns are not in the official version), the sections must close over them.",
            "Olival", "AB 159 deg", Machines.Puma, Machines.Sprayer8, Puma) { Obstacles = true };
        yield return new JobSpec("J3", "Baetas, trailed seed drill of 19 sections",
            "6.3 ha, a trailed drill 7.98 m wide in 19 sections of 42 cm (PGN 229 above 16), AB line at 8°.",
            "Baetas", "1 Principal AB 8 deg", Machines.Puma, Machines.Seeder19, Puma);
        yield return new JobSpec("J4", "Couto Ribeira, trailed 21 m cultivator",
            "12.9 ha, a trailed 21 m implement in 7 sections of 3 m, AB line at 119°, headland 42 m.",
            "Couto Ribeira", "1 Principal AB 119 deg", Machines.Puma, Machines.Trailing21, Puma) { HeadlandM = 42, UTurnRadius = 10 };
        yield return new JobSpec("J5", "Quinta Torta, 8 m sprayer, AB at 0°",
            "15.2 ha, the biggest field, the sprayer along its 0° line.",
            "Quinta Torta", "AB 0°", Machines.Puma, Machines.Sprayer8, Puma);
        yield return new JobSpec("J6", "Tapada da Casa with its 82 obstacles, 8 m sprayer",
            "14.6 ha and the most obstacles, AB line at 162°.",
            "Tapada da Casa", "1 Principal AB 162 deg", Machines.Puma, Machines.Sprayer8, Puma) { Obstacles = true };
    }
}
