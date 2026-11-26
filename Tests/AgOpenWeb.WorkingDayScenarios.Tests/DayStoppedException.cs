// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.WorkingDayScenarios.Tests;

/// <summary>The day ended on purpose before its end (<see cref="Day.Until"/>, <see cref="Day.StopOn"/>):
/// what was looked for has happened. Not a crash; the report is written on what ran.</summary>
public sealed class DayStoppedException(string why) : Exception(why);
