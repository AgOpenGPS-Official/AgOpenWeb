// AgValoniaGPS
// Copyright (C) 2024-2025 AgValoniaGPS Contributors
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
using AgValoniaGPS.Models;

namespace AgValoniaGPS.Services;

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
    /// Load a complete field (metadata, boundary, background image)
    /// </summary>
    Field LoadField(string fieldDirectory);

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
    /// Reads each field's origin from <c>field.json</c> if present,
    /// otherwise falls back to the legacy <c>Field.txt</c> writer.
    /// Fields with a (0,0) origin or unreadable metadata are skipped.
    /// </remarks>
    IReadOnlyList<NearbyField> FindFieldsNear(
        string fieldsRootDirectory,
        double latitude,
        double longitude,
        double maxKm);
}
