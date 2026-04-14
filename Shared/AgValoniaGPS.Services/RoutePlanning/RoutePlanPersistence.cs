// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.RoutePlanning;

namespace AgValoniaGPS.Services.RoutePlanning;

/// <summary>
/// Save/load RoutePlan to JSON files in the field directory.
/// File: {fieldDir}/RoutePlan.json
/// </summary>
public static class RoutePlanPersistence
{
    private const string FileName = "RoutePlan.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public static void Save(RoutePlan plan, string fieldDirectory)
    {
        try
        {
            var dto = ToDto(plan);
            var json = JsonSerializer.Serialize(dto, JsonOptions);
            var path = Path.Combine(fieldDirectory, FileName);
            File.WriteAllText(path, json);
            Debug.WriteLine($"[RoutePlan] Saved to {path} ({plan.SwathCount} swaths, {plan.TurnCount} turns)");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RoutePlan] Save failed: {ex.Message}");
        }
    }

    public static RoutePlan? Load(string fieldDirectory)
    {
        try
        {
            var path = Path.Combine(fieldDirectory, FileName);
            if (!File.Exists(path)) return null;

            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<RoutePlanDto>(json, JsonOptions);
            if (dto == null) return null;

            var plan = FromDto(dto);
            Debug.WriteLine($"[RoutePlan] Loaded from {path} ({plan.SwathCount} swaths, {plan.TurnCount} turns)");
            return plan;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RoutePlan] Load failed: {ex.Message}");
            return null;
        }
    }

    public static bool Exists(string fieldDirectory)
    {
        return File.Exists(Path.Combine(fieldDirectory, FileName));
    }

    // DTO classes for clean JSON serialization (Vec3 doesn't serialize well directly)

    private class RoutePlanDto
    {
        public string Pattern { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public List<RouteSegmentDto> Segments { get; set; } = new();
    }

    private class RouteSegmentDto
    {
        public string Type { get; set; } = "";
        public int SwathIndex { get; set; }
        public bool IsReverse { get; set; }
        public bool IsTurnValid { get; set; }
        public string? TurnPathType { get; set; }
        public List<double[]> Waypoints { get; set; } = new(); // [easting, northing, heading]
    }

    private static RoutePlanDto ToDto(RoutePlan plan)
    {
        var dto = new RoutePlanDto
        {
            Pattern = plan.Pattern,
            CreatedAt = plan.CreatedAt,
        };

        foreach (var seg in plan.Segments)
        {
            var segDto = new RouteSegmentDto
            {
                Type = seg.Type.ToString(),
                SwathIndex = seg.SwathIndex,
                IsReverse = seg.IsReverse,
                IsTurnValid = seg.IsTurnValid,
                TurnPathType = seg.TurnPathType,
            };
            foreach (var wp in seg.Waypoints)
                segDto.Waypoints.Add(new[] { wp.Easting, wp.Northing, wp.Heading });

            dto.Segments.Add(segDto);
        }

        return dto;
    }

    private static RoutePlan FromDto(RoutePlanDto dto)
    {
        var plan = new RoutePlan
        {
            Pattern = dto.Pattern,
            CreatedAt = dto.CreatedAt,
        };

        foreach (var segDto in dto.Segments)
        {
            var seg = new RouteSegment
            {
                Type = Enum.TryParse<RouteSegmentType>(segDto.Type, out var t) ? t : RouteSegmentType.Swath,
                SwathIndex = segDto.SwathIndex,
                IsReverse = segDto.IsReverse,
                IsTurnValid = segDto.IsTurnValid,
                TurnPathType = segDto.TurnPathType,
            };
            foreach (var wp in segDto.Waypoints)
            {
                if (wp.Length >= 3)
                    seg.Waypoints.Add(new Vec3(wp[0], wp[1], wp[2]));
            }
            seg.Length = CalculateLength(seg.Waypoints);
            plan.Segments.Add(seg);
        }

        return plan;
    }

    private static double CalculateLength(List<Vec3> waypoints)
    {
        double len = 0;
        for (int i = 1; i < waypoints.Count; i++)
        {
            double dx = waypoints[i].Easting - waypoints[i - 1].Easting;
            double dy = waypoints[i].Northing - waypoints[i - 1].Northing;
            len += Math.Sqrt(dx * dx + dy * dy);
        }
        return len;
    }
}
