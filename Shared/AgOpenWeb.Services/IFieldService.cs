// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using AgOpenWeb.Models;

namespace AgOpenWeb.Services;

/// <summary>
/// Service interface for field management (loading, saving, listing fields)
/// </summary>
public interface IFieldService
{
    /// <summary>
    /// Event fired when the active field changes
    /// </summary>
    event EventHandler<Field?>? ActiveFieldChanged;

    /// <summary>
    /// Currently active field
    /// </summary>
    Field? ActiveField { get; }

    /// <summary>
    /// Get list of available field names
    /// </summary>
    List<string> GetAvailableFields(string fieldsRootDirectory);

    /// <summary>
    /// Load a complete field (metadata, boundary, background image) from field.geojson,
    /// first importing (and deleting) any AgOpenGPS field files in the folder.
    /// </summary>
    Field LoadField(string fieldDirectory);

    /// <summary>
    /// Read a field without changing its folder: field.geojson, or the AgOpenGPS files if it
    /// hasn't been imported yet. For callers that only look at a field (lists, origins).
    /// </summary>
    Field PeekField(string fieldDirectory);

    /// <summary>A field's tracks, read like <see cref="PeekField"/> (either format, no changes).</summary>
    List<Models.Track.Track> PeekTracks(string fieldDirectory);

    /// <summary>A field's flags, read like <see cref="PeekField"/>.</summary>
    List<Flag> PeekFlags(string fieldDirectory);

    /// <summary>A field's headland lines, read like <see cref="PeekField"/>.</summary>
    Models.Guidance.HeadlandLine PeekHeadlandLine(string fieldDirectory);

    /// <summary>A field's background image placement, read like <see cref="PeekField"/>.</summary>
    FieldBackground? PeekBackground(string fieldDirectory);

    /// <summary>
    /// Save a complete field (metadata, boundary, background image)
    /// </summary>
    void SaveField(Field field);

    /// <summary>
    /// Create a new empty field
    /// </summary>
    Field CreateField(string fieldsRootDirectory, string fieldName, Position originPosition);

    /// <summary>
    /// Delete a field
    /// </summary>
    void DeleteField(string fieldDirectory);

    /// <summary>
    /// Check if a field exists
    /// </summary>
    bool FieldExists(string fieldDirectory);

    /// <summary>
    /// Set the active field
    /// </summary>
    void SetActiveField(Field? field);

    /// <summary>
    /// List fields within <paramref name="maxKm"/> kilometres of
    /// (<paramref name="latitude"/>, <paramref name="longitude"/>),
    /// ordered ascending by distance. Powers the Distance column on the
    /// StartWorkSession dialog and the InField shortcut.
    /// </summary>
    /// <remarks>
    /// Reads each field's origin with <see cref="PeekField"/> (field.geojson, or the AgOpenGPS
    /// files of a field not yet imported), without changing the folder.
    /// Fields with a (0,0) origin or unreadable metadata are skipped.
    /// </remarks>
    IReadOnlyList<NearbyField> FindFieldsNear(
        string fieldsRootDirectory,
        double latitude,
        double longitude,
        double maxKm);
}
