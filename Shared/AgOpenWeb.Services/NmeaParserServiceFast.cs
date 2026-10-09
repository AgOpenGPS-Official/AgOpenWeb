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
using System.Buffers.Text;
using System.Runtime.CompilerServices;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.GPS;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services;

/// <summary>
/// Zero-copy NMEA parser: one datagram in, one complete <see cref="VehicleState"/> fix out.
/// Parses directly from the byte buffer with no heap allocations (sub-millisecond, matching
/// the Teensy firmware approach).
///
/// <para><b>One fix = one receiver epoch.</b> Every sentence this parser accepts carries a
/// whole fix — position, speed, heading, roll, quality — measured together by the device
/// that sent it: <c>$PANDA</c>/<c>$PAOGI</c> from an AiO board (the Teensy pairs the GPS
/// epoch with its IMU sample), <c>$KSXT</c> from a Bynav/Unicore receiver (its own INS).
/// The host only decides <i>when</i> a fix is used, never <i>what</i> is in it: nothing is
/// paired across sentences or across devices here, because on a non-real-time host that
/// pairing would depend on OS scheduling (that is AgIO's GGA + IMU-PGN problem). A receiver
/// that spreads a fix over several sentences (GGA/VTG/HDT/AVR) needs an epoch assembler
/// that emits when the receiver's burst is complete — a separate decoder, not a change to
/// this dispatch.</para>
/// </summary>
public class NmeaParserServiceFast
{
    private readonly IGpsService _gpsService;
    private readonly ConfigurationStore _configStore;

    // Pre-allocated GpsData to avoid allocation per parse
    private GpsData _gpsData;

    // Field indices for PANDA/PAOGI
    private const int FIELD_TIME = 1;
    private const int FIELD_LAT = 2;
    private const int FIELD_LAT_DIR = 3;
    private const int FIELD_LON = 4;
    private const int FIELD_LON_DIR = 5;
    private const int FIELD_FIX = 6;
    private const int FIELD_SATS = 7;
    private const int FIELD_HDOP = 8;
    private const int FIELD_ALT = 9;
    private const int FIELD_AGE = 10;
    private const int FIELD_SPEED = 11;
    private const int FIELD_HEADING = 12;
    private const int FIELD_ROLL = 13;
    private const int FIELD_PITCH = 14;
    private const int FIELD_YAW_RATE = 15;

    private const int MIN_PANDA_FIELDS = 15;

    // Field indices for KSXT (Unicore N4 reference manual, table 7-131; the manual's IDs
    // are 1-based with $KSXT as ID 1), read the way AgIO's ParseKSXT does. Lat/lon are
    // signed decimal degrees, velocity is km/h. Field 6 is the antenna baseline's pitch,
    // which is the vehicle's roll with the two antennas mounted across the cab (the
    // AgOpenGPS dual convention). Fields 10/11 are the receiver's own position/heading
    // quality codes, not GGA values — see KsxtFixQuality. Field 13 is the master
    // antenna's satellite count. Field 20 is reserved in the Unicore spec; AgIO reads it as
    // the correction age and it's empty on Unicore (age 0), kept for parity.
    private const int KSXT_LON = 2;
    private const int KSXT_LAT = 3;
    private const int KSXT_ALT = 4;
    private const int KSXT_HEADING = 5;
    private const int KSXT_ROLL = 6;
    private const int KSXT_SPEED = 8;
    private const int KSXT_POS_QUALITY = 10;
    private const int KSXT_HEADING_QUALITY = 11;
    private const int KSXT_SATS = 13;
    private const int KSXT_AGE = 20;
    // Through the master satellite count. Unicore prints 22 fields, a Bynav T1-FD 21; the
    // trailing ENU/reserved fields are optional here.
    private const int MIN_KSXT_FIELDS = 14;
    private const int KSXT_QUALITY_RTK_FIXED = 3;

    // Comma table size: the longest accepted sentence (KSXT, 22 fields) plus slack.
    private const int MAX_FIELDS = 32;

    // Sentence type identifiers (after $)
    private static ReadOnlySpan<byte> PANDA => "PANDA"u8;
    private static ReadOnlySpan<byte> PAOGI => "PAOGI"u8;
    private static ReadOnlySpan<byte> KSXT => "KSXT,"u8;

    public NmeaParserServiceFast(IGpsService gpsService, ConfigurationStore configStore)
    {
        _gpsService = gpsService;
        _configStore = configStore;
        _gpsData = new GpsData();
    }

    /// <summary>
    /// Parse NMEA sentence directly from byte buffer and hand the fix to the
    /// <see cref="IGpsService"/> as a <see cref="GpsData"/>. Same decoders as
    /// <see cref="ParseIntoState"/>, which is the pipeline's path; this one is for
    /// tools and tests.
    /// </summary>
    /// <param name="buffer">Raw UDP receive buffer</param>
    /// <param name="length">Number of valid bytes in buffer</param>
    /// <returns>True if parsed successfully</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ParseBuffer(byte[] buffer, int length)
    {
        return ParseSpan(buffer.AsSpan(0, length));
    }

    /// <summary>As <see cref="ParseBuffer"/>, from a span.</summary>
    public bool ParseSpan(ReadOnlySpan<byte> data)
    {
        var state = new VehicleState();
        if (!ParseIntoState(data, ref state, _configStore)) return false;

        _gpsData = new GpsData
        {
            CurrentPosition = new Position
            {
                Latitude = state.Latitude,
                Longitude = state.Longitude,
                Altitude = state.Altitude,
                Speed = state.Speed,
                Heading = state.Heading
            },
            FixQuality = state.FixQuality,
            SatellitesInUse = state.Satellites,
            Hdop = state.Hdop,
            DifferentialAge = state.DifferentialAge,
            HasDualHeading = state.HasDualHeading,
            SentenceType = state.SentenceType,
            Timestamp = DateTime.UtcNow
        };

        _gpsService.UpdateGpsData(_gpsData);
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Zero-Copy VehicleState Parsing (AutoSteer Pipeline)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parse one NMEA sentence directly into a VehicleState struct: frame check
    /// ($, checksum), then the decoder for the sentence type. Zero allocations.
    /// </summary>
    /// <param name="data">Raw NMEA data</param>
    /// <param name="state">VehicleState struct to populate (passed by ref)</param>
    /// <returns>True if parsed successfully</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static bool ParseIntoState(ReadOnlySpan<byte> data, ref VehicleState state,
        ConfigurationStore configStore)
        => TryParseIntoState(data, ref state, configStore, out _);

    /// <summary>
    /// As <see cref="ParseIntoState"/>, saying why a line was refused: the System Data card
    /// counts bad checksums (a corrupted or mis-split line) apart from sentences this build
    /// does not decode (a receiver printing <c>$GNGGA</c>, or a <c>#</c> log).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static bool TryParseIntoState(ReadOnlySpan<byte> data, ref VehicleState state,
        ConfigurationStore configStore, out NmeaParseResult result)
    {
        state.MarkParseStart();

        // A '#' line is a receiver's CRC-framed log (Unicore INSPVAXA…): a known shape,
        // not decoded yet — the card should name it rather than call it garbage.
        if (data.Length > 0 && data[0] == '#')
        {
            result = NmeaParseResult.UnknownSentence;
            return false;
        }

        // Minimum valid: $PANDA,... = at least 20 bytes, and it must start with $
        if (data.Length < 20 || data[0] != '$')
        {
            result = NmeaParseResult.BadFrame;
            return false;
        }

        // Find checksum marker
        int asterisk = data.IndexOf((byte)'*');
        if (asterisk < 10)
        {
            result = NmeaParseResult.BadFrame;
            return false;
        }

        // Validate checksum (XOR of bytes between $ and *)
        if (!ValidateChecksum(data, asterisk))
        {
            result = NmeaParseResult.BadChecksum;
            return false;
        }

        // Comma table for the body (up to the asterisk)
        var body = data.Slice(0, asterisk);
        Span<int> commas = stackalloc int[MAX_FIELDS];
        int fieldCount = SplitFields(body, commas);

        // Dispatch on the sentence id (bytes 1-5 after $)
        var sentenceType = data.Slice(1, 5);
        bool ok;
        if (sentenceType.SequenceEqual(PANDA)) ok = DecodePanda(body, commas, fieldCount, ref state, configStore);
        else if (sentenceType.SequenceEqual(PAOGI)) ok = DecodePaogi(body, commas, fieldCount, ref state, configStore);
        else if (sentenceType.SequenceEqual(KSXT)) ok = DecodeKsxt(body, commas, fieldCount, ref state, configStore);
        else
        {
            result = NmeaParseResult.UnknownSentence;
            return false;
        }

        if (!ok)
        {
            result = NmeaParseResult.BadFields;
            return false;
        }

        // Pre-compute heading in radians for guidance calculations
        state.HeadingRadians = state.Heading * (Math.PI / 180.0);

        state.MarkParseEnd();
        result = NmeaParseResult.Accepted;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ValidateChecksum(ReadOnlySpan<byte> data, int asteriskPos)
    {
        // Calculate XOR checksum of bytes between $ and *
        byte checksum = 0;
        for (int i = 1; i < asteriskPos; i++)
        {
            checksum ^= data[i];
        }

        // Parse provided checksum (2 hex digits after *)
        if (asteriskPos + 2 >= data.Length) return false;

        byte providedHigh = HexCharToNibble(data[asteriskPos + 1]);
        byte providedLow = HexCharToNibble(data[asteriskPos + 2]);

        if (providedHigh == 0xFF || providedLow == 0xFF) return false;

        byte provided = (byte)((providedHigh << 4) | providedLow);
        return checksum == provided;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte HexCharToNibble(byte c)
    {
        if (c >= '0' && c <= '9') return (byte)(c - '0');
        if (c >= 'A' && c <= 'F') return (byte)(c - 'A' + 10);
        if (c >= 'a' && c <= 'f') return (byte)(c - 'a' + 10);
        return 0xFF; // Invalid
    }

    /// <summary>
    /// Fill <paramref name="commas"/> with a virtual comma before the first field, every
    /// comma position, and the end of the body; returns the number of fields.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SplitFields(ReadOnlySpan<byte> body, Span<int> commas)
    {
        int commaCount = 0;
        commas[commaCount++] = -1; // Virtual comma before first field

        for (int i = 0; i < body.Length && commaCount < commas.Length - 1; i++)
        {
            if (body[i] == ',')
                commas[commaCount++] = i;
        }

        // Add end position as final "comma"
        commas[commaCount++] = body.Length;
        return commaCount - 1;
    }

    // ─── $PANDA / $PAOGI ──────────────────────────────────────────────────

    /// <summary>
    /// $PANDA: single antenna + IMU from an AiO board. Field 12 is the IMU heading as
    /// <c>(int)(degrees * 10)</c> with sentinel 65535 for "no IMU"; field 13 is the IMU
    /// roll as <c>(int)(degrees * 10)</c> (the firmware's currentData.roll is
    /// pre-multiplied by 10 at the IMU layer — see
    /// Firmware_Teensy_AiO_26/lib/aio_navigation/IMUProcessor.cpp).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool DecodePanda(ReadOnlySpan<byte> data, Span<int> commas, int fieldCount,
        ref VehicleState state, ConfigurationStore configStore)
    {
        if (fieldCount < MIN_PANDA_FIELDS) return false;
        DecodeAogPositionFields(data, commas, ref state);

        // Not a dual heading: HasDualHeading is false, so "Dual GPS" ignores it (#157:
        // AgIO puts PANDA field 12 in imuHeading, PAOGI field 12 in headingTrueDual).
        state.HasDualHeading = false;
        state.SentenceType = GpsSentenceType.Panda;
        var headingField = GetField(data, commas, FIELD_HEADING);
        var rollField = GetField(data, commas, FIELD_ROLL);

        int rawHeading = 0;
        bool imuValid = false;
        if (headingField.Length > 0
            && Utf8Parser.TryParse(headingField, out rawHeading, out _)
            && rawHeading != 65535)
        {
            state.ImuHeading = rawHeading * 0.1;
            imuValid = true;
        }
        else
        {
            state.ImuHeading = 0;
        }
        state.ImuValid = imuValid;
        // Seed primary heading from IMU so first-cycle / standstill has a
        // sensible default. Pipeline's fix-to-fix overrides at any real speed.
        state.Heading = imuValid ? state.ImuHeading : 0;

        if (imuValid && rollField.Length > 0
            && Utf8Parser.TryParse(rollField, out int rawRoll, out _))
        {
            state.Roll = rawRoll * 0.1;
            ApplyAhrsRollCalibration(ref state.Roll, configStore);
        }
        else
        {
            state.Roll = 0;
        }

        DecodeAogAttitudeFields(data, commas, ref state);
        return true;
    }

    /// <summary>
    /// $PAOGI: dual antenna from an AiO board. Field 12 is the dual-antenna heading and
    /// field 13 the dual roll, both float decimal degrees. Dual antenna is ground truth —
    /// no IMU fusion needed, so ImuHeading stays 0 and ImuValid stays false.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool DecodePaogi(ReadOnlySpan<byte> data, Span<int> commas, int fieldCount,
        ref VehicleState state, ConfigurationStore configStore)
    {
        if (fieldCount < MIN_PANDA_FIELDS) return false;
        DecodeAogPositionFields(data, commas, ref state);

        state.HasDualHeading = true;
        state.SentenceType = GpsSentenceType.Paogi;
        var headingField = GetField(data, commas, FIELD_HEADING);
        var rollField = GetField(data, commas, FIELD_ROLL);

        if (headingField.Length > 0)
        {
            Utf8Parser.TryParse(headingField, out state.Heading, out _);
        }
        else
        {
            state.Heading = 0;
        }
        state.ImuHeading = 0;
        state.ImuValid = false;

        if (rollField.Length > 0)
        {
            Utf8Parser.TryParse(rollField, out state.Roll, out _);
            ApplyAhrsRollCalibration(ref state.Roll, configStore);
        }
        else
        {
            state.Roll = 0;
        }

        DecodeAogAttitudeFields(data, commas, ref state);
        return true;
    }

    /// <summary>Fields 1-11, identical between $PANDA and $PAOGI (the GGA part).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DecodeAogPositionFields(ReadOnlySpan<byte> data, Span<int> commas, ref VehicleState state)
    {
        // Parse latitude (field 2): DDMM.MMMMM
        var latField = GetField(data, commas, FIELD_LAT);
        if (latField.Length > 0)
        {
            state.Latitude = ParseLatLon(latField);
        }

        // Latitude direction (field 3): N or S
        var latDirField = GetField(data, commas, FIELD_LAT_DIR);
        if (latDirField.Length > 0 && latDirField[0] == 'S')
            state.Latitude = -state.Latitude;

        // Longitude (field 4): DDDMM.MMMMM
        var lonField = GetField(data, commas, FIELD_LON);
        if (lonField.Length > 0)
        {
            state.Longitude = ParseLatLon(lonField);
        }

        // Longitude direction (field 5): E or W
        var lonDirField = GetField(data, commas, FIELD_LON_DIR);
        if (lonDirField.Length > 0 && lonDirField[0] == 'W')
            state.Longitude = -state.Longitude;

        // Fix quality (field 6)
        var fixField = GetField(data, commas, FIELD_FIX);
        if (fixField.Length > 0)
        {
            Utf8Parser.TryParse(fixField, out state.FixQuality, out _);
        }

        // Satellites (field 7)
        var satsField = GetField(data, commas, FIELD_SATS);
        if (satsField.Length > 0)
        {
            Utf8Parser.TryParse(satsField, out state.Satellites, out _);
        }

        // HDOP (field 8)
        var hdopField = GetField(data, commas, FIELD_HDOP);
        if (hdopField.Length > 0)
        {
            Utf8Parser.TryParse(hdopField, out state.Hdop, out _);
        }

        // Altitude (field 9)
        var altField = GetField(data, commas, FIELD_ALT);
        if (altField.Length > 0)
        {
            Utf8Parser.TryParse(altField, out state.Altitude, out _);
        }

        // Age of differential (field 10)
        var ageField = GetField(data, commas, FIELD_AGE);
        if (ageField.Length > 0)
        {
            Utf8Parser.TryParse(ageField, out state.DifferentialAge, out _);
        }

        // Speed in knots (field 11) - convert to m/s
        var speedField = GetField(data, commas, FIELD_SPEED);
        if (speedField.Length > 0)
        {
            Utf8Parser.TryParse(speedField, out state.Speed, out _);
            state.Speed *= 0.514444; // knots to m/s
        }
    }

    /// <summary>Fields 14-15 (pitch, yaw rate), same format in $PANDA and $PAOGI.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DecodeAogAttitudeFields(ReadOnlySpan<byte> data, Span<int> commas, ref VehicleState state)
    {
        // Pitch angle in degrees (field 14)
        var pitchField = GetField(data, commas, FIELD_PITCH);
        if (pitchField.Length > 0)
        {
            Utf8Parser.TryParse(pitchField, out state.Pitch, out _);
        }

        // Yaw rate in degrees/second (field 15)
        var yawField = GetField(data, commas, FIELD_YAW_RATE);
        if (yawField.Length > 0)
        {
            Utf8Parser.TryParse(yawField, out state.YawRate, out _);
        }
    }

    // ─── $KSXT ────────────────────────────────────────────────────────────

    /// <summary>
    /// $KSXT: Bynav / Unicore dual-antenna receiver with its own INS, sent straight from
    /// the receiver over UDP (no AiO board). One sentence carries the whole fix, so it is
    /// decoded like $PAOGI: dual heading, roll from the antenna baseline, no IMU fusion.
    /// Field map and quality rules follow AgIO's ParseKSXT so a receiver set up for AgIO
    /// behaves the same here. No HDOP in the sentence (left 0, which passes the validator);
    /// pitch and yaw rate are not read.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool DecodeKsxt(ReadOnlySpan<byte> data, Span<int> commas, int fieldCount,
        ref VehicleState state, ConfigurationStore configStore)
    {
        if (fieldCount < MIN_KSXT_FIELDS) return false;

        // AgIO takes the sentence only when time, lon, lat, height and heading are present.
        var lonField = GetField(data, commas, KSXT_LON);
        var latField = GetField(data, commas, KSXT_LAT);
        var altField = GetField(data, commas, KSXT_ALT);
        var headingField = GetField(data, commas, KSXT_HEADING);
        if (GetField(data, commas, FIELD_TIME).Length == 0 || lonField.Length == 0 || latField.Length == 0
            || altField.Length == 0 || headingField.Length == 0)
            return false;

        // Signed decimal degrees, not DDMM.MMMM
        if (!Utf8Parser.TryParse(lonField, out state.Longitude, out _)) return false;
        if (!Utf8Parser.TryParse(latField, out state.Latitude, out _)) return false;
        Utf8Parser.TryParse(altField, out state.Altitude, out _);

        // Dual-antenna heading, float degrees
        Utf8Parser.TryParse(headingField, out state.Heading, out _);
        state.HasDualHeading = true;
        state.SentenceType = GpsSentenceType.Ksxt;
        state.ImuHeading = 0;
        state.ImuValid = false;

        // Speed in km/h - convert to m/s
        var speedField = GetField(data, commas, KSXT_SPEED);
        state.Speed = 0;
        if (speedField.Length > 0 && Utf8Parser.TryParse(speedField, out state.Speed, out _))
        {
            state.Speed /= 3.6;
        }

        // Position quality: the receiver's own code, mapped to the GGA scale
        int posQuality = 0;
        var posQualityField = GetField(data, commas, KSXT_POS_QUALITY);
        if (posQualityField.Length > 0) Utf8Parser.TryParse(posQualityField, out posQuality, out _);
        state.FixQuality = KsxtFixQuality(posQuality);

        // Roll only when the heading solution is RTK fixed; otherwise it's noise (AgIO
        // sends float.MinValue, which AgOpenGPS reads as roll 0).
        int headingQuality = 0;
        var headingQualityField = GetField(data, commas, KSXT_HEADING_QUALITY);
        if (headingQualityField.Length > 0) Utf8Parser.TryParse(headingQualityField, out headingQuality, out _);
        var rollField = GetField(data, commas, KSXT_ROLL);
        state.Roll = 0;
        if (headingQuality == KSXT_QUALITY_RTK_FIXED && rollField.Length > 0
            && Utf8Parser.TryParse(rollField, out state.Roll, out _))
        {
            ApplyAhrsRollCalibration(ref state.Roll, configStore);
        }
        else
        {
            state.Roll = 0;
        }

        // Satellites
        state.Satellites = 0;
        var satsField = GetField(data, commas, KSXT_SATS);
        if (satsField.Length > 0) Utf8Parser.TryParse(satsField, out state.Satellites, out _);

        // Age of differential, seconds (reserved field; present but empty on Unicore/Bynav)
        state.DifferentialAge = 0;
        if (fieldCount > KSXT_AGE)
        {
            var ageField = GetField(data, commas, KSXT_AGE);
            if (ageField.Length > 0) Utf8Parser.TryParse(ageField, out state.DifferentialAge, out _);
        }

        state.Hdop = 0;
        state.Pitch = 0;
        state.YawRate = 0;
        return true;
    }

    /// <summary>
    /// KSXT position quality → GGA fix quality, as AgIO maps it: 0 invalid, 1 single,
    /// 2 → 5 (RTK float), 3 → 4 (RTK fixed). Any other code is treated as no fix rather
    /// than guessed at, so an unknown receiver state can't steer; extend this when a
    /// capture shows one.
    /// </summary>
    internal static int KsxtFixQuality(int posQuality) => posQuality switch
    {
        0 => 0,
        1 => 1,
        2 => 5,
        3 => 4,
        _ => 0,
    };

    // ─── Shared helpers ───────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> GetField(ReadOnlySpan<byte> data, Span<int> commas, int fieldIndex)
    {
        int start = commas[fieldIndex] + 1;
        int end = commas[fieldIndex + 1];
        int length = end - start;

        if (length <= 0 || start >= data.Length) return ReadOnlySpan<byte>.Empty;

        return data.Slice(start, length);
    }

    /// <summary>
    /// Parse latitude/longitude from NMEA format: DDMM.MMMMM or DDDMM.MMMMM
    /// Zero allocation - works directly on byte span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double ParseLatLon(ReadOnlySpan<byte> field)
    {
        // Find decimal point
        int decimalPos = -1;
        for (int i = 0; i < field.Length; i++)
        {
            if (field[i] == '.')
            {
                decimalPos = i;
                break;
            }
        }

        if (decimalPos < 2) return 0;

        // Degrees are everything before (decimalPos - 2)
        int degreeEnd = decimalPos - 2;

        // Parse degrees (1-3 digits)
        int degrees = 0;
        for (int i = 0; i < degreeEnd; i++)
        {
            degrees = degrees * 10 + (field[i] - '0');
        }

        // Parse minutes (rest of the field including decimal)
        double minutes = 0;
        if (Utf8Parser.TryParse(field.Slice(degreeEnd), out minutes, out _))
        {
            return degrees + (minutes / 60.0);
        }

        return degrees;
    }

    /// <summary>
    /// Apply the operator-calibrated AHRS roll transform: invert sign if
    /// <c>IsRollInvert</c> is set, then subtract <c>RollZero</c>. Mirrors
    /// the WAS post-process in <c>SmartWasCalibrationService</c>. Without
    /// this, the wizard's roll gauge and the guidance pipeline both see
    /// raw uncalibrated IMU roll, and the operator's "Zero Roll" tap in
    /// the Roll-calibration wizard step has no effect on live readings.
    /// </summary>
    private static void ApplyAhrsRollCalibration(ref double roll, ConfigurationStore configStore)
    {
        var ahrs = configStore.Ahrs;
        if (ahrs.IsRollInvert) roll = -roll;
        roll -= ahrs.RollZero;
    }
}
