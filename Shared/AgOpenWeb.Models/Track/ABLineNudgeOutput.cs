// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Models.Track;

/// <summary>
/// Output from AB line nudging calculation.
/// </summary>
/// <param name="NewPointA">New Point A after nudging.</param>
/// <param name="NewPointB">New Point B after nudging.</param>
public record ABLineNudgeOutput(Vec2 NewPointA, Vec2 NewPointB);
