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
using AgOpenWeb.Services.Gps;
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
    // Standard sentence types (bytes 3-5 after a two-letter talker)
    private static ReadOnlySpan<byte> GGA => "GGA"u8;
    private static ReadOnlySpan<byte> GNS => "GNS"u8;
    private static ReadOnlySpan<byte> VTG => "VTG"u8;
    private static ReadOnlySpan<byte> HPR => "HPR"u8;
    private static ReadOnlySpan<byte> HDT => "HDT"u8;
    private static ReadOnlySpan<byte> THS => "THS"u8;
    // Unicore logs (name between # or % and the first comma)
    private static ReadOnlySpan<byte> INSPVAXA => "INSPVAXA"u8;
    private static ReadOnlySpan<byte> INSPVAA => "INSPVAA"u8;
    private static ReadOnlySpan<byte> INSPVAXSA => "INSPVAXSA"u8;
    private static ReadOnlySpan<byte> INSPVASA => "INSPVASA"u8;

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
        => TryParseIntoState(data, ref state, configStore, null, out result);

    /// <summary>
    /// As <see cref="TryParseIntoState(ReadOnlySpan{byte}, ref VehicleState, ConfigurationStore, out NmeaParseResult)"/>,
    /// with the epoch assembler that takes a receiver's standard sentences (GGA/GNS, VTG,
    /// HPR, HDT, THS). A member returns false with <see cref="NmeaParseResult.EpochMember"/>
    /// until the epoch closes, when the fix lands in <paramref name="state"/>. Without an
    /// assembler those sentences are unknown.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static bool TryParseIntoState(ReadOnlySpan<byte> data, ref VehicleState state,
        ConfigurationStore configStore, NmeaEpochAssembler? epochs, out NmeaParseResult result)
    {
        state.MarkParseStart();

        // A '#' or '%' line is a Unicore / NovAtel log with a CRC-32: the INSPVAX family is
        // one fused fix per epoch (UM981); anything else is named, not called garbage.
        if (data.Length > 0 && (data[0] == '#' || data[0] == '%'))
            return TryParseUnicore(data, ref state, configStore, out result);

        // The shortest sentence that means anything is $GPHDT,h,T*hh; each decoder checks
        // its own field count. It must start with $ and carry a checksum.
        if (data.Length < 9 || data[0] != '$')
        {
            result = NmeaParseResult.BadFrame;
            return false;
        }

        // Find checksum marker
        int asterisk = data.IndexOf((byte)'*');
        if (asterisk < 7)
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
        else if (IsStandardTalker(data))
        {
            // $GPGGA, $GNVTG, $GNHPR, $INHPR…: one member of a multi-sentence epoch.
            var type = data.Slice(3, 3);
            EpochMemberData member = default;
            bool known = true, decoded;
            if (type.SequenceEqual(GGA)) decoded = DecodeGga(body, commas, fieldCount, ref member);
            else if (type.SequenceEqual(GNS)) decoded = DecodeGns(body, commas, fieldCount, ref member);
            else if (type.SequenceEqual(VTG)) decoded = DecodeVtg(body, commas, fieldCount, ref member);
            else if (type.SequenceEqual(HPR)) decoded = DecodeHpr(body, commas, fieldCount, ref member);
            else if (type.SequenceEqual(HDT)) decoded = DecodeHdt(body, commas, fieldCount, ref member);
            else if (type.SequenceEqual(THS)) decoded = DecodeThs(body, commas, fieldCount, ref member);
            else { known = false; decoded = false; }

            if (!known || epochs == null)
            {
                result = NmeaParseResult.UnknownSentence;
                return false;
            }
            if (!decoded)
            {
                result = NmeaParseResult.BadFields;
                return false;
            }
            // An IMU / heading sensor talker (IN, HE): its heading is the IMU's, as in $PANDA.
            member.FromImuTalker = (data[1] == 'I' && data[2] == 'N') || (data[1] == 'H' && data[2] == 'E');
            if (!epochs.Add(in member, ref state, configStore))
            {
                result = NmeaParseResult.EpochMember;
                return false;
            }
            ok = true;
        }
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

    // ─── Standard sentences: members of a receiver epoch ──────────────────

    /// <summary>$ + two talker letters + a three-letter type + a comma: $GNGGA, $INHPR…</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsStandardTalker(ReadOnlySpan<byte> data) =>
        data.Length > 7 && data[6] == ','
        && IsUpper(data[1]) && IsUpper(data[2]) && IsUpper(data[3]) && IsUpper(data[4]) && IsUpper(data[5]);

    private static bool IsUpper(byte b) => b >= 'A' && b <= 'Z';

    /// <summary>hhmmss.ss → centiseconds since midnight, or -1 when empty or malformed.</summary>
    private static int ParseUtc(ReadOnlySpan<byte> field)
    {
        if (field.Length < 6) return -1;
        if (!Utf8Parser.TryParse(field, out double hhmmss, out _)) return -1;
        return (int)Math.Round(hhmmss * 100);
    }

    /// <summary>
    /// GGA: utc, lat, N/S, lon, E/W, fix, sats, hdop, alt, M, sep, M, age, station. The
    /// age is what $KSXT lacks. Empty position fields read as 0 with the fix as printed
    /// (a receiver without a fix prints them empty with fix 0).
    /// </summary>
    private static bool DecodeGga(ReadOnlySpan<byte> data, Span<int> commas, int fieldCount, ref EpochMemberData m)
    {
        if (fieldCount < 10) return false;
        m.Kind = EpochMembers.Position;
        m.UtcCentiseconds = ParseUtc(GetField(data, commas, 1));
        DecodePositionFields(data, commas, 2, ref m);
        var fix = GetField(data, commas, 6);
        if (fix.Length > 0) Utf8Parser.TryParse(fix, out m.FixQuality, out _);
        var sats = GetField(data, commas, 7);
        if (sats.Length > 0) Utf8Parser.TryParse(sats, out m.Satellites, out _);
        var hdop = GetField(data, commas, 8);
        if (hdop.Length > 0) Utf8Parser.TryParse(hdop, out m.Hdop, out _);
        var alt = GetField(data, commas, 9);
        if (alt.Length > 0) Utf8Parser.TryParse(alt, out m.Altitude, out _);
        if (fieldCount > 13)
        {
            var age = GetField(data, commas, 13);
            if (age.Length > 0) Utf8Parser.TryParse(age, out m.DifferentialAge, out _);
        }
        return true;
    }

    /// <summary>
    /// GNS: utc, lat, N/S, lon, E/W, mode (one letter per constellation), sats, hdop, alt,
    /// sep, age, station. The best mode letter sets the fix on the GGA scale: R fixed 4,
    /// F float 5, D/P differential 2, A autonomous 1, E estimated 6, N none 0.
    /// </summary>
    private static bool DecodeGns(ReadOnlySpan<byte> data, Span<int> commas, int fieldCount, ref EpochMemberData m)
    {
        if (fieldCount < 10) return false;
        m.Kind = EpochMembers.Position;
        m.IsGns = true;
        m.UtcCentiseconds = ParseUtc(GetField(data, commas, 1));
        DecodePositionFields(data, commas, 2, ref m);
        m.FixQuality = 0;
        foreach (byte mode in GetField(data, commas, 6))
        {
            int q = mode switch { (byte)'R' => 4, (byte)'F' => 5, (byte)'D' => 2, (byte)'P' => 2, (byte)'A' => 1, (byte)'E' => 6, _ => 0 };
            if (Rank(q) > Rank(m.FixQuality)) m.FixQuality = q;
        }
        var sats = GetField(data, commas, 7);
        if (sats.Length > 0) Utf8Parser.TryParse(sats, out m.Satellites, out _);
        var hdop = GetField(data, commas, 8);
        if (hdop.Length > 0) Utf8Parser.TryParse(hdop, out m.Hdop, out _);
        var alt = GetField(data, commas, 9);
        if (alt.Length > 0) Utf8Parser.TryParse(alt, out m.Altitude, out _);
        if (fieldCount > 11)
        {
            var age = GetField(data, commas, 11);
            if (age.Length > 0) Utf8Parser.TryParse(age, out m.DifferentialAge, out _);
        }
        return true;
    }

    /// <summary>Fix quality ordered by how good it is: fixed, float, differential/estimated, single, none.</summary>
    private static int Rank(int fix) => fix switch { 4 => 5, 5 => 4, 2 => 3, 6 => 2, 1 => 1, _ => 0 };

    /// <summary>lat, N/S, lon, E/W at <paramref name="first"/> (ddmm.mmmm → signed degrees).</summary>
    private static void DecodePositionFields(ReadOnlySpan<byte> data, Span<int> commas, int first, ref EpochMemberData m)
    {
        var lat = GetField(data, commas, first);
        var ns = GetField(data, commas, first + 1);
        var lon = GetField(data, commas, first + 2);
        var ew = GetField(data, commas, first + 3);
        m.Latitude = lat.Length > 0 ? ParseLatLon(lat) : 0;
        if (ns.Length > 0 && ns[0] == 'S') m.Latitude = -m.Latitude;
        m.Longitude = lon.Length > 0 ? ParseLatLon(lon) : 0;
        if (ew.Length > 0 && ew[0] == 'W') m.Longitude = -m.Longitude;
    }

    /// <summary>VTG: track true, T, track magnetic, M, speed knots, N, speed km/h, K[, mode]. Speed from km/h, else knots.</summary>
    private static bool DecodeVtg(ReadOnlySpan<byte> data, Span<int> commas, int fieldCount, ref EpochMemberData m)
    {
        if (fieldCount < 6) return false;
        m.Kind = EpochMembers.Vtg;
        m.UtcCentiseconds = -1;
        var track = GetField(data, commas, 1);
        if (track.Length > 0) Utf8Parser.TryParse(track, out m.TrackDeg, out _);
        var kmh = fieldCount > 7 ? GetField(data, commas, 7) : ReadOnlySpan<byte>.Empty;
        var knots = GetField(data, commas, 5);
        m.SpeedMps = 0;
        if (kmh.Length > 0 && Utf8Parser.TryParse(kmh, out double v, out _)) m.SpeedMps = v / 3.6;
        else if (knots.Length > 0 && Utf8Parser.TryParse(knots, out v, out _)) m.SpeedMps = v * 0.514444;
        return true;
    }

    /// <summary>
    /// HPR (Unicore N4 manual table 7-42): utc, heading, pitch, roll, QF, sats, age,
    /// station. QF is on the GGA scale: heading valid when 4 (fixed) or 5 (float), roll
    /// only when 4. The baseline's pitch is the vehicle's roll with the antennas across the
    /// cab (the AgOpenGPS convention, as the $KSXT decoder reads it).
    /// </summary>
    private static bool DecodeHpr(ReadOnlySpan<byte> data, Span<int> commas, int fieldCount, ref EpochMemberData m)
    {
        if (fieldCount < 6) return false;
        m.Kind = EpochMembers.Hpr;
        m.UtcCentiseconds = ParseUtc(GetField(data, commas, 1));
        int qf = 0;
        var qfField = GetField(data, commas, 5);
        if (qfField.Length > 0) Utf8Parser.TryParse(qfField, out qf, out _);
        var heading = GetField(data, commas, 2);
        m.HeadingValid = (qf == 4 || qf == 5) && heading.Length > 0 && Utf8Parser.TryParse(heading, out m.HeadingDeg, out _);
        var pitch = GetField(data, commas, 3);
        m.RollValid = qf == 4 && pitch.Length > 0 && Utf8Parser.TryParse(pitch, out m.RollDeg, out _);
        return true;
    }

    /// <summary>HDT: heading, T.</summary>
    private static bool DecodeHdt(ReadOnlySpan<byte> data, Span<int> commas, int fieldCount, ref EpochMemberData m)
    {
        if (fieldCount < 2) return false;
        m.Kind = EpochMembers.Hdt;
        m.UtcCentiseconds = -1;
        var heading = GetField(data, commas, 1);
        m.HeadingValid = heading.Length > 0 && Utf8Parser.TryParse(heading, out m.HeadingDeg, out _);
        return true;
    }

    /// <summary>THS: heading, mode. Valid unless the mode says invalid (V) or is missing.</summary>
    private static bool DecodeThs(ReadOnlySpan<byte> data, Span<int> commas, int fieldCount, ref EpochMemberData m)
    {
        if (fieldCount < 2) return false;
        m.Kind = EpochMembers.Ths;
        m.UtcCentiseconds = -1;
        var heading = GetField(data, commas, 1);
        var mode = fieldCount > 2 ? GetField(data, commas, 2) : ReadOnlySpan<byte>.Empty;
        bool modeOk = mode.Length > 0 && mode[0] != 'V';
        m.HeadingValid = modeOk && heading.Length > 0 && Utf8Parser.TryParse(heading, out m.HeadingDeg, out _);
        return true;
    }

    // ─── Unicore / NovAtel logs: #NAME,header;body*crc ─────────────────────

    /// <summary>
    /// A <c>#</c> (long header) or <c>%</c> (short header) ASCII log: the CRC-32 over
    /// everything between the lead and the <c>*</c> must match the 8 hex digits after it;
    /// the header ends at <c>;</c>. INSPVAXA / INSPVAA (UM981) are decoded as one fix.
    /// </summary>
    private static bool TryParseUnicore(ReadOnlySpan<byte> data, ref VehicleState state,
        ConfigurationStore configStore, out NmeaParseResult result)
    {
        int asterisk = data.LastIndexOf((byte)'*');
        if (data.Length < 20 || asterisk < 10 || asterisk + 8 >= data.Length + 1 || data.Length < asterisk + 9)
        {
            result = NmeaParseResult.BadFrame;
            return false;
        }
        uint provided = 0;
        for (int i = 1; i <= 8; i++)
        {
            byte nibble = HexCharToNibble(data[asterisk + i]);
            if (nibble == 0xFF) { result = NmeaParseResult.BadFrame; return false; }
            provided = (provided << 4) | nibble;
        }
        if (UnicoreCrc32.Compute(data.Slice(1, asterisk - 1)) != provided)
        {
            result = NmeaParseResult.BadChecksum;
            return false;
        }
        int semicolon = data.IndexOf((byte)';');
        int firstComma = data.IndexOf((byte)',');
        if (semicolon < 0 || semicolon > asterisk || firstComma < 2 || firstComma > semicolon)
        {
            result = NmeaParseResult.BadFrame;
            return false;
        }
        var name = data.Slice(1, firstComma - 1);
        if (!(name.SequenceEqual(INSPVAXA) || name.SequenceEqual(INSPVAA)
              || name.SequenceEqual(INSPVAXSA) || name.SequenceEqual(INSPVASA)))
        {
            result = NmeaParseResult.UnknownSentence;
            return false;
        }
        var body = data.Slice(semicolon + 1, asterisk - semicolon - 1);
        Span<int> commas = stackalloc int[MAX_FIELDS];
        int fieldCount = SplitFields(body, commas);
        if (!DecodeInspvax(body, commas, fieldCount, ref state, configStore))
        {
            result = NmeaParseResult.BadFields;
            return false;
        }
        state.HeadingRadians = state.Heading * (Math.PI / 180.0);
        state.MarkParseEnd();
        result = NmeaParseResult.Accepted;
        return true;
    }

    // INSPVAX body (UM981 manual table 2-11, after the ';'): ins status, position type,
    // lat, lon, height, undulation, north / east / up velocity, roll, pitch, azimuth, then
    // standard deviations, extended status and time since update (INSPVAA stops at azimuth).
    private const int INS_STATUS = 0;
    private const int INS_POS_TYPE = 1;
    private const int INS_LAT = 2;
    private const int INS_LON = 3;
    private const int INS_HEIGHT = 4;
    private const int INS_VEL_NORTH = 6;
    private const int INS_VEL_EAST = 7;
    private const int INS_ROLL = 9;
    private const int INS_PITCH = 10;
    private const int INS_AZIMUTH = 11;
    private const int MIN_INSPVAX_FIELDS = 12;

    /// <summary>
    /// One fused INS fix: position, azimuth as the heading (the receiver's own, so no IMU
    /// fusion), speed from the north/east velocities, the INS roll and pitch (the IMU is in
    /// the receiver, mounted along the vehicle). No satellite count, HDOP or age in the log.
    /// Fix quality from the position type, none while the INS is inactive, as the v26
    /// firmware maps it.
    /// </summary>
    private static bool DecodeInspvax(ReadOnlySpan<byte> body, Span<int> commas, int fieldCount,
        ref VehicleState state, ConfigurationStore configStore)
    {
        if (fieldCount < MIN_INSPVAX_FIELDS) return false;
        var lat = GetField(body, commas, INS_LAT);
        var lon = GetField(body, commas, INS_LON);
        if (!Utf8Parser.TryParse(lat, out state.Latitude, out _)) return false;
        if (!Utf8Parser.TryParse(lon, out state.Longitude, out _)) return false;
        state.Altitude = 0;
        Utf8Parser.TryParse(GetField(body, commas, INS_HEIGHT), out state.Altitude, out _);

        var status = GetField(body, commas, INS_STATUS);
        var posType = GetField(body, commas, INS_POS_TYPE);
        state.FixQuality = status.SequenceEqual("INS_INACTIVE"u8) ? 0 : InsFixQuality(posType);

        double vn = 0, ve = 0;
        Utf8Parser.TryParse(GetField(body, commas, INS_VEL_NORTH), out vn, out _);
        Utf8Parser.TryParse(GetField(body, commas, INS_VEL_EAST), out ve, out _);
        state.Speed = Math.Sqrt(vn * vn + ve * ve);

        state.Heading = 0;
        Utf8Parser.TryParse(GetField(body, commas, INS_AZIMUTH), out state.Heading, out _);
        state.HasDualHeading = true;
        state.ImuHeading = 0;
        state.ImuValid = false;

        state.Roll = 0;
        if (Utf8Parser.TryParse(GetField(body, commas, INS_ROLL), out state.Roll, out _))
            ApplyAhrsRollCalibration(ref state.Roll, configStore);
        state.Pitch = 0;
        Utf8Parser.TryParse(GetField(body, commas, INS_PITCH), out state.Pitch, out _);
        state.YawRate = 0;
        state.Satellites = 0;
        state.Hdop = 0;
        state.DifferentialAge = 0;
        state.SentenceType = GpsSentenceType.Inspvax;
        return true;
    }

    /// <summary>INS position type → GGA fix quality: INS_RTKFIXED 4, INS_RTKFLOAT 5, INS_PSRDIFF 2, INS_PSRSP 1, any other INS_* 1, else 0.</summary>
    internal static int InsFixQuality(ReadOnlySpan<byte> posType)
    {
        if (posType.SequenceEqual("INS_RTKFIXED"u8)) return 4;
        if (posType.SequenceEqual("INS_RTKFLOAT"u8)) return 5;
        if (posType.SequenceEqual("INS_PSRDIFF"u8)) return 2;
        if (posType.SequenceEqual("INS_PSRSP"u8)) return 1;
        return posType.StartsWith("INS"u8) ? 1 : 0;
    }

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
    internal static void ApplyAhrsRollCalibration(ref double roll, ConfigurationStore configStore)
    {
        var ahrs = configStore.Ahrs;
        if (ahrs.IsRollInvert) roll = -roll;
        roll -= ahrs.RollZero;
    }
}
