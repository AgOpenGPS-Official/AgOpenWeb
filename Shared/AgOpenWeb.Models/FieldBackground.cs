// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.Models;

/// <summary>
/// A field's background image (aerial imagery): the image file in the field folder and where it
/// sits, as its north-west and south-east WGS84 corners. <see cref="Mercator"/> is set for an
/// image captured from a Web-Mercator tile service, so the map can place it without the
/// north-south stretch a linear fit between the corners gives.
/// </summary>
public sealed record FieldBackground(
    string ImageFile,
    double NwLatitude, double NwLongitude,
    double SeLatitude, double SeLongitude,
    MercatorBounds? Mercator)
{
    /// <summary>The image file name AgOpenWeb uses in a field folder.</summary>
    public const string DefaultImageFile = "background.png";
}

/// <summary>Web-Mercator (EPSG:3857) bounds of an image, in metres.</summary>
public readonly record struct MercatorBounds(double MinX, double MaxX, double MinY, double MaxY);
