// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;

namespace AgOpenWeb.Services.AutoSteer;

/// <summary>
/// Builds PGN packets for transmission to AgOpenGPS hardware modules.
/// Uses thread-local buffers for zero allocations in the hot path.
///
/// Format follows AgOpenGPS standard: [0x80, 0x81, Source, PGN, Length, Data..., CRC]
/// </summary>
public static class PgnBuilder
{
    // Standard AgOpenGPS header
    public const byte HEADER1 = 0x80;
    public const byte HEADER2 = 0x81;
    public const byte SOURCE = 0x7F;  // AgIO/AgOpenGPS source

    // PGN identifiers (from PgnNumbers)
    public const byte PGN_AUTOSTEER = 0xFE;      // 254 - AutoSteer Data
    public const byte PGN_MACHINE = 0xEF;        // 239 - Machine Data
    public const byte PGN_SECTIONS_64 = 0xE5;    // 229 - 64-section on/off + L/R speed
    public const byte PGN_STEER_SETTINGS = 0xFC; // 252 - Steer Settings
    public const byte PGN_STEER_CONFIG = 0xFB;   // 251 - Steer Config
    public const byte PGN_MACHINE_CONFIG = 0xEE;  // 238 - Machine Config
    public const byte PGN_MACHINE_PINS = 0xEC;   // 236 - Machine Pin Config
    public const byte PGN_STEER_DATA = 0xFD;     // 253 - Steer Data FROM Module
    public const byte PGN_SENSOR_DATA = 0xFA;    // 250 - Sensor Data FROM Module
    public const byte PGN_CORRECTED_POSITION = 0x64; // 100 - Corrected position (seed monitors, rate control)
    public const byte PGN_GPS_DATA = 0xD6;       // 214 - AgIO's GPS data (GPS_Out)
    public const byte SOURCE_GPS = 0x7C;         // AgIO sends PGN 214 as the GPS module
    public const byte PGN_VEHICLE_GEOMETRY = 0xF7; // 247 - Vehicle geometry (AgOpenWeb, #387)

    // Buffer sizes: header(2) + source(1) + pgn(1) + length(1) + data(N) + crc(1)
    public const int AUTOSTEER_PGN_SIZE = 14;       // 5 header + 8 data + 1 crc
    public const int MACHINE_PGN_SIZE = 14;         // 5 header + 8 data + 1 crc
    public const int SECTIONS_64_PGN_SIZE = 16;     // 5 header + 10 data + 1 crc
    public const int STEER_SETTINGS_PGN_SIZE = 14;  // 5 header + 8 data + 1 crc
    public const int STEER_CONFIG_PGN_SIZE = 11;    // 5 header + 5 data + 1 crc
    public const int MACHINE_CONFIG_PGN_SIZE = 14;  // 5 header + 8 data + 1 crc
    public const int MACHINE_PINS_PGN_SIZE = 30;    // 5 header + 24 data + 1 crc
    public const int CORRECTED_POSITION_PGN_SIZE = 30; // 5 header + 24 data + 1 crc
    public const int GPS_DATA_PGN_SIZE = 57;        // 5 header + 51 data + 1 crc
    public const int VEHICLE_GEOMETRY_PGN_SIZE = 25; // 5 header + 19 data + 1 crc
    public const byte VEHICLE_GEOMETRY_VERSION = 1;

    // Thread-local buffers to avoid allocation
    [ThreadStatic]
    private static byte[]? _autoSteerBuffer;

    [ThreadStatic]
    private static byte[]? _machineBuffer;

    [ThreadStatic]
    private static byte[]? _sections64Buffer;

    [ThreadStatic]
    private static byte[]? _steerSettingsBuffer;

    [ThreadStatic]
    private static byte[]? _steerConfigBuffer;

    [ThreadStatic]
    private static byte[]? _correctedPositionBuffer;

    [ThreadStatic]
    private static byte[]? _gpsDataBuffer;

    /// <summary>
    /// Build PGN 254 (Steer Data) from VehicleState.
    /// Format per PGN 5.6 spec: [0x80, 0x81, 0x7F, 0xFE, 8, Speed(2), Status, SteerAngle(2), XTE, SC1-8, SC9-16, CRC]
    ///
    /// Byte 5-6:  Speed (high/low)
    /// Byte 7:    Status
    /// Byte 8-9:  steerAngle * 100 (high/low)
    /// Byte 10:   xte (cross-track error, single byte)
    /// Byte 11:   SC1to8 (sections 1-8 bitmask)
    /// Byte 12:   SC9to16 (sections 9-16 bitmask)
    /// Byte 13:   CRC
    ///
    /// When IsInFreeDriveMode is true, overrides speed/status/angle for testing:
    /// - Speed set to 8.0 km/h (fake speed to allow motor operation)
    /// - Status set to 1 so the firmware drives toward the commanded angle
    /// - SteerAngle from FreeDriveSteerAngle instead of guidance
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte[] BuildAutoSteerPgn(ref VehicleState state)
    {
        // Use thread-local buffer to avoid allocation
        _autoSteerBuffer ??= new byte[AUTOSTEER_PGN_SIZE];
        var buf = _autoSteerBuffer;

        // Header
        buf[0] = HEADER1;
        buf[1] = HEADER2;
        buf[2] = SOURCE;
        buf[3] = PGN_AUTOSTEER;
        buf[4] = 8;  // Data length

        // Check for Free Drive mode (test mode from config panel)
        if (state.IsInFreeDriveMode)
        {
            // Free Drive: fake speed of 8.0 km/h to allow motor operation
            // Little-endian: low byte first (Arduino/Teensy convention)
            ushort freeSpeed = 80;  // 8.0 km/h * 10
            buf[5] = (byte)(freeSpeed & 0xFF);        // low byte
            buf[6] = (byte)(freeSpeed >> 8);          // high byte

            // Status 1 = steer (AgOpenGPS: "turn on status to operate").
            buf[7] = 1;

            // Use free drive steer angle instead of guidance angle
            // Little-endian: low byte first
            short freeDriveAngle = (short)(state.FreeDriveSteerAngle * 100);
            buf[8] = (byte)(freeDriveAngle & 0xFF);   // low byte
            buf[9] = (byte)(freeDriveAngle >> 8);     // high byte

            // XTE = 0 in free drive mode
            buf[10] = 127;  // 0 + 127 offset

            // Section states (still send real values)
            buf[11] = (byte)(state.SectionStates & 0xFF);
            buf[12] = (byte)((state.SectionStates >> 8) & 0xFF);
        }
        else
        {
            // Normal mode: use guidance values
            // Little-endian: low byte first (Arduino/Teensy convention)

            // Speed - using km/h * 10 format (2 bytes)
            ushort speedInt = (ushort)(state.SpeedKmh * 10);
            buf[5] = (byte)(speedInt & 0xFF);         // low byte
            buf[6] = (byte)(speedInt >> 8);           // high byte

            // Status: 1 = steer, 0 = don't, exactly as AgOpenGPS sends it
            // (Position.designer.cs). The firmware steers on bit 0 (AiO v26
            // AutosteerProcessor: status & 0x01; AIO v4: guidanceStatus == 1).
            // This used to put the module's own echoed steer state in bit 0 and
            // AgOpenWeb's engage in bit 2, which no firmware reads (#125).
            buf[7] = (byte)(state.IsAutoSteerEngaged && !state.IsSteerPaused ? 1 : 0);

            // Steer angle * 100 (signed, 2 bytes)
            short angleInt = (short)(state.SteerAngle * 100);
            buf[8] = (byte)(angleInt & 0xFF);         // low byte
            buf[9] = (byte)(angleInt >> 8);           // high byte

            // XTE - cross-track error (single byte, clamped to -127 to 127 cm)
            int xte = (int)(state.CrossTrackError * 100); // meters to cm
            xte = Math.Clamp(xte, -127, 127);
            buf[10] = (byte)(sbyte)xte;

            // Section states (16 bits = 2 bytes)
            buf[11] = (byte)(state.SectionStates & 0xFF);         // Sections 1-8
            buf[12] = (byte)((state.SectionStates >> 8) & 0xFF);  // Sections 9-16
        }

        return WithCrc(buf);
    }

    /// <summary>
    /// Build PGN 239 (Machine Data) from VehicleState.
    /// Format per PGN 5.6 spec: [0x80, 0x81, 0x7F, 0xEF, 8, uturn, speed*10, hydLift, Tram, GeoStop, ***, SC1-8, SC9-16, CRC]
    ///
    /// Byte 5:  uturn (U-turn state)
    /// Byte 6:  speed * 10 (single byte)
    /// Byte 7:  hydLift (hydraulic lift state)
    /// Byte 8:  Tram (tramline state)
    /// Byte 9:  Geo Stop (geo-fence stop)
    /// Byte 10: Reserved
    /// Byte 11: SC1to8 (sections 1-8 bitmask)
    /// Byte 12: SC9to16 (sections 9-16 bitmask)
    /// Byte 13: CRC
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte[] BuildMachinePgn(ref VehicleState state, byte uturn = 0, byte hydLift = 0, byte tram = 0, byte geoStop = 0)
    {
        // Use thread-local buffer
        _machineBuffer ??= new byte[MACHINE_PGN_SIZE];
        var buf = _machineBuffer;

        // Header
        buf[0] = HEADER1;
        buf[1] = HEADER2;
        buf[2] = SOURCE;
        buf[3] = PGN_MACHINE;
        buf[4] = 8;  // Data length

        // U-turn state
        buf[5] = uturn;

        // Speed * 10 (single byte, max 25.5 km/h in this format)
        int speedInt = (int)(state.SpeedKmh * 10);
        buf[6] = (byte)Math.Clamp(speedInt, 0, 255);

        // Hydraulic lift state
        buf[7] = hydLift;

        // Tramline state
        buf[8] = tram;

        // Geo Stop
        buf[9] = geoStop;

        // Reserved
        buf[10] = 0;

        // Section states (16 bits = 2 bytes)
        buf[11] = (byte)(state.SectionStates & 0xFF);         // Sections 1-8
        buf[12] = (byte)((state.SectionStates >> 8) & 0xFF);  // Sections 9-16

        return WithCrc(buf);
    }

    /// <summary>
    /// Build PGN 229 (0xE5) — 64-section on/off plus left/right speed.
    /// Sent in addition to PGN 239 when more than 16 sections are configured;
    /// the firmware reconciles the overlap on sections 1–16.
    ///
    /// Byte 5:  SC1to8   (sections 1–8 bitmask)
    /// Byte 6:  SC9to16  (sections 9–16)
    /// Byte 7:  SC17to24
    /// Byte 8:  SC25to32
    /// Byte 9:  SC33to40
    /// Byte 10: SC41to48
    /// Byte 11: SC49to56
    /// Byte 12: SC57to64
    /// Byte 13: Lspeed (speed * 10, clamped 0–255)
    /// Byte 14: Rspeed (speed * 10, clamped 0–255)
    /// Byte 15: CRC
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte[] BuildSection64Pgn(ref VehicleState state)
    {
        _sections64Buffer ??= new byte[SECTIONS_64_PGN_SIZE];
        var buf = _sections64Buffer;

        // Header
        buf[0] = HEADER1;
        buf[1] = HEADER2;
        buf[2] = SOURCE;
        buf[3] = PGN_SECTIONS_64;
        buf[4] = 10;  // Data length

        // 64 section bits, little-endian (section 1 = bit 0 of byte 5)
        ulong sections = state.SectionStates;
        for (int i = 0; i < 8; i++)
            buf[5 + i] = (byte)((sections >> (8 * i)) & 0xFF);

        // L/R speed mirror PGN 239's speed byte (speed * 10, clamped to a byte)
        byte speed = (byte)Math.Clamp((int)(state.SpeedKmh * 10), 0, 255);
        buf[13] = speed;
        buf[14] = speed;

        return WithCrc(buf);
    }

    /// <summary>
    /// Build PGN 100 (0x64) — corrected position, AgOpenGPS's 24-byte layout
    /// (Position.designer.cs "Corrected Position"). Seed monitors, rate controllers
    /// and GPS_Out read it.
    ///
    /// Bytes 5–12:  longitude, double (little-endian)
    /// Bytes 13–20: latitude, double
    /// Bytes 21–28: heading, degrees, double
    /// Byte 29:     CRC
    /// </summary>
    public static byte[] BuildCorrectedPositionPgn(double latitude, double longitude, double headingDegrees)
    {
        _correctedPositionBuffer ??= new byte[CORRECTED_POSITION_PGN_SIZE];
        var buf = _correctedPositionBuffer;

        buf[0] = HEADER1;
        buf[1] = HEADER2;
        buf[2] = SOURCE;
        buf[3] = PGN_CORRECTED_POSITION;
        buf[4] = 24;  // Data length

        System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(5, 8), longitude);
        System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(13, 8), latitude);
        System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(21, 8), headingDegrees);

        return WithCrc(buf);
    }

    /// <summary>
    /// Build PGN 214 (0xD6) — the receiver's data as AgIO sends it to AgOpenGPS and
    /// GPS_Out after parsing the NMEA (AgIO NMEA.Designer.cs). GPS_Out builds its GGA, VTG,
    /// RMC and ZDA sentences from it (#364). A field the sentence doesn't carry holds its
    /// type's MaxValue, as in AgIO.
    ///
    /// Bytes 5–12:  longitude, double
    /// Bytes 13–20: latitude, double
    /// Bytes 21–24: dual-antenna heading, float ($PAOGI, $KSXT, INS, the simulator)
    /// Bytes 25–28: single-antenna heading, float (course over ground; not $PANDA)
    /// Bytes 29–32: speed, km/h, float
    /// Bytes 33–36: roll, degrees, float (not $PANDA: its roll is the IMU roll below)
    /// Bytes 37–40: altitude, m, float
    /// Bytes 41–42: satellites, ushort
    /// Byte 43:     fix quality
    /// Bytes 44–45: HDOP × 100, ushort
    /// Bytes 46–47: differential age × 100, ushort
    /// Bytes 48–55: $PANDA's IMU heading × 10 (ushort), roll × 10, pitch, yaw rate (short),
    ///              as the sentence carries them
    /// Byte 56:     CRC
    /// </summary>
    public static byte[] BuildGpsDataPgn(GpsData data)
    {
        _gpsDataBuffer ??= new byte[GPS_DATA_PGN_SIZE];
        var buf = _gpsDataBuffer;
        var pos = data.CurrentPosition;
        bool panda = data.SentenceType == GpsSentenceType.Panda;
        bool imu = panda && data.ImuValid;

        buf[0] = HEADER1;
        buf[1] = HEADER2;
        buf[2] = SOURCE_GPS;
        buf[3] = PGN_GPS_DATA;
        buf[4] = GPS_DATA_PGN_SIZE - 6;

        var s = buf.AsSpan();
        System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(s.Slice(5, 8), pos.Longitude);
        System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(s.Slice(13, 8), pos.Latitude);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(s.Slice(21, 4),
            data.HasDualHeading ? (float)pos.Heading : float.MaxValue);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(s.Slice(25, 4),
            !data.HasDualHeading && !panda ? (float)pos.Heading : float.MaxValue);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(s.Slice(29, 4), (float)(pos.Speed * 3.6));
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(s.Slice(33, 4),
            panda ? float.MaxValue : (float)data.ImuRoll);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(s.Slice(37, 4), (float)pos.Altitude);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(41, 2), UShort(data.SatellitesInUse));
        buf[43] = (byte)Math.Clamp(data.FixQuality, 0, 255);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(44, 2), UShort(data.Hdop * 100));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(46, 2), UShort(data.DifferentialAge * 100));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(48, 2),
            imu ? UShort(data.ImuHeading * 10) : ushort.MaxValue);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(s.Slice(50, 2), imu ? Short(data.ImuRoll * 10) : short.MaxValue);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(s.Slice(52, 2), panda ? Short(data.ImuPitch) : short.MaxValue);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(s.Slice(54, 2), panda ? Short(data.ImuYawRate) : short.MaxValue);

        return WithCrc(buf);

        static ushort UShort(double v) => (ushort)Math.Clamp(Math.Round(v), 0, ushort.MaxValue - 1);
        static short Short(double v) => (short)Math.Clamp(Math.Round(v), short.MinValue, short.MaxValue - 1);
    }

    /// <summary>
    /// Build PGN 238 (0xEE) Machine Config - hydraulic settings and user values.
    /// Sent to machine module when config changes (not periodic).
    ///
    /// Byte 5:  Raise time (seconds)
    /// Byte 6:  Lower time (seconds)
    /// Byte 7:  Enable hydraulic (0/1)
    /// Byte 8:  set0 bitfield (bit0=InvertRelay, bit1=HydEnabled)
    /// Byte 9:  User1 value (0-255)
    /// Byte 10: User2 value (0-255)
    /// Byte 11: User3 value (0-255)
    /// Byte 12: User4 value (0-255)
    /// </summary>
    public static byte[] BuildMachineConfigPgn(MachineConfig config)
    {
        var buf = new byte[MACHINE_CONFIG_PGN_SIZE];

        buf[0] = HEADER1;
        buf[1] = HEADER2;
        buf[2] = SOURCE;
        buf[3] = PGN_MACHINE_CONFIG;
        buf[4] = 8;

        buf[5] = (byte)Math.Clamp(config.RaiseTime, 0, 255);
        buf[6] = (byte)Math.Clamp(config.LowerTime, 0, 255);
        buf[7] = config.HydraulicLiftEnabled ? (byte)1 : (byte)0;

        // set0 bitfield
        byte set0 = 0;
        if (config.InvertRelay) set0 |= 0x01;
        if (config.HydraulicLiftEnabled) set0 |= 0x02;
        buf[8] = set0;

        buf[9] = (byte)Math.Clamp(config.User1Value, 0, 255);
        buf[10] = (byte)Math.Clamp(config.User2Value, 0, 255);
        buf[11] = (byte)Math.Clamp(config.User3Value, 0, 255);
        buf[12] = (byte)Math.Clamp(config.User4Value, 0, 255);

        return WithCrc(buf);
    }

    /// <summary>
    /// Build PGN 247 (0xF7) Vehicle geometry, an AgOpenWeb PGN (#387): what a module needs
    /// to know about the vehicle — a steer module estimating the wheel angle from the heading
    /// rate needs the wheelbase, a receiver's INS (UM981) the IMU-to-antenna lever arm. Sent
    /// with PGN 251/252 and again when the steer module comes back after a silence.
    ///
    /// Lengths in cm, little-endian; positions from the pivot (rear axle centre): pivot =
    /// ahead (+), offset = right (+), height = above the ground.
    /// Byte 5:      layout version (1); a module reads the fields it knows and skips the rest
    /// Byte 6:      flags, bit 0: IMU position entered (else bytes 17-22 are 0, not a position)
    /// Bytes 7-8:   wheelbase, uint16
    /// Bytes 9-10:  track width, uint16
    /// Bytes 11-12: antenna height, uint16
    /// Bytes 13-14: antenna pivot, int16
    /// Bytes 15-16: antenna offset, int16
    /// Bytes 17-18: IMU pivot, int16
    /// Bytes 19-20: IMU height, uint16
    /// Bytes 21-22: IMU offset, int16
    /// Byte 23:     max steer angle, degrees
    /// Byte 24:     CRC
    /// </summary>
    public static byte[] BuildVehicleGeometryPgn(VehicleConfig v)
    {
        var buf = new byte[VEHICLE_GEOMETRY_PGN_SIZE];
        buf[0] = HEADER1;
        buf[1] = HEADER2;
        buf[2] = SOURCE;
        buf[3] = PGN_VEHICLE_GEOMETRY;
        buf[4] = VEHICLE_GEOMETRY_PGN_SIZE - 6;

        buf[5] = VEHICLE_GEOMETRY_VERSION;
        bool imu = v.ImuPivot != 0 || v.ImuHeight != 0 || v.ImuOffset != 0;
        buf[6] = imu ? (byte)1 : (byte)0;
        PutCm(buf, 7, v.Wheelbase, unsigned: true);
        PutCm(buf, 9, v.TrackWidth, unsigned: true);
        PutCm(buf, 11, v.AntennaHeight, unsigned: true);
        PutCm(buf, 13, v.AntennaPivot, unsigned: false);
        PutCm(buf, 15, v.AntennaOffset, unsigned: false);
        PutCm(buf, 17, v.ImuPivot, unsigned: false);
        PutCm(buf, 19, v.ImuHeight, unsigned: true);
        PutCm(buf, 21, v.ImuOffset, unsigned: false);
        buf[23] = (byte)Math.Clamp((int)Math.Round(v.MaxSteerAngle), 0, 255);

        return WithCrc(buf);
    }

    private static void PutCm(byte[] buf, int at, double metres, bool unsigned)
    {
        int cm = (int)Math.Round(metres * 100);
        int value = unsigned ? Math.Clamp(cm, 0, ushort.MaxValue) : Math.Clamp(cm, short.MinValue, short.MaxValue);
        buf[at] = (byte)(value & 0xFF);
        buf[at + 1] = (byte)((value >> 8) & 0xFF);
    }

    /// <summary>
    /// Build PGN 236 (0xEC) Machine Pin Config - 24 relay pin assignments.
    /// Sent to machine module when pin config changes (not periodic).
    ///
    /// Bytes 5-28: Pin 0-23 function assignments (PinFunction enum value per byte)
    /// </summary>
    public static byte[] BuildMachinePinsPgn(MachineConfig config)
    {
        var buf = new byte[MACHINE_PINS_PGN_SIZE];

        buf[0] = HEADER1;
        buf[1] = HEADER2;
        buf[2] = SOURCE;
        buf[3] = PGN_MACHINE_PINS;
        buf[4] = 24;

        var pins = config.PinAssignments;
        for (int i = 0; i < 24; i++)
        {
            buf[5 + i] = (byte)(i < pins.Length ? pins[i] : 0);
        }

        return WithCrc(buf);
    }

    /// <summary>
    /// Calculate CRC as sum of bytes (matching PgnMessage.CalculateCRC)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    /// <summary>
    /// Build PGN 241 (0xF1) for the ISOBUS task controller: section control mode, one
    /// data byte, 1 = automatic (the task controller drives the implement's sections),
    /// 0 = manual (#312). AgOpenGPS CISOBUS.RequestSectionControlEnabled, with the CRC.
    /// </summary>
    public static byte[] BuildIsobusSectionControlPgn(bool enabled)
    {
        var buf = new byte[7];
        buf[0] = HEADER1;
        buf[1] = HEADER2;
        buf[2] = SOURCE;
        buf[3] = PgnNumbers.ISOBUS_SECTION_CONTROL;
        buf[4] = 1;
        buf[5] = (byte)(enabled ? 1 : 0);
        return WithCrc(buf);
    }

    private static byte CalculateCrc(byte[] data, int start, int length)
    {
        byte crc = 0;
        for (int i = start; i < start + length; i++)
        {
            crc += data[i];
        }
        return crc;
    }

    /// <summary>
    /// Stamp the trailing CRC into a fully-populated packet and return it. The
    /// last byte is the sum of bytes [2 .. len-2] (source through last data
    /// byte), matching <see cref="CalculateCrc"/> and PgnMessage.CalculateCRC.
    /// </summary>
    /// <remarks>
    /// Every builder in this class returns through here, so the CRC offset and
    /// span are derived from the buffer length instead of being hand-written per
    /// PGN. That removes the failure mode where a packet grows a data byte and
    /// its checksum keeps summing the old span. Operates in place — safe for the
    /// pooled buffers the hot-path builders reuse.
    /// </remarks>
    private static byte[] WithCrc(byte[] packet)
    {
        packet[^1] = CalculateCrc(packet, 2, packet.Length - 3);
        return packet;
    }

    // ===== Module network config (AgIO parity: FormUDP.cs / UDP.designer.cs) =====
    // The layouts below reproduce AgIO's wire format so the existing AiO board
    // install base responds correctly. AgIO itself leaves a stale placeholder
    // (0x47) in the trailing CRC slot and never recomputes it — UDP.designer.cs:82
    // rewrites helloFromAgIO[5] and leaves the checksum untouched. Modules gate on
    // the header + PGN + magic bytes (data[5]/[6]) and ignore the CRC, which is why
    // that placeholder has always been accepted. We send a correctly computed CRC
    // instead: firmware that ignores the byte is unaffected, and firmware that does
    // verify it now passes rather than relying on luck.
    //
    // This is send-side only. Do NOT gate the inbound path on ValidateChecksum:
    // some modules transmit a placeholder CRC of their own, and rejecting those
    // packets takes the whole link down.

    /// <summary>
    /// Build PGN 202 — "scan request" broadcast that asks every module to reply
    /// with its IP/subnet (PGN 203). AgIO layout (FormUDP.cs:137) with a computed
    /// CRC: { 0x80, 0x81, 0x7F, 202, 3, 202, 202, 5, CRC }.
    /// </summary>
    public static byte[] BuildScanRequest()
        => WithCrc(new byte[] { HEADER1, HEADER2, SOURCE, PgnNumbers.SCAN_REQUEST, 3, 202, 202, 5, 0 });

    /// <summary>
    /// Build PGN 200 — the "hello" packet modules watch for to confirm the host is
    /// alive. AgIO layout (UDP.designer.cs:76) with a computed CRC:
    /// { 0x80, 0x81, 0x7F, 200, 3, 56, 0, 0, CRC }.
    /// </summary>
    public static byte[] BuildHelloPacket()
        => WithCrc(new byte[] { HEADER1, HEADER2, SOURCE, PgnNumbers.HELLO_FROM_AGIO, 3, 56, 0, 0, 0 });

    /// <summary>
    /// Build PGN 201 — "set subnet" broadcast. Changes the first three IP octets
    /// (the /24) on ALL modules at once; the host octet is preserved by each
    /// module. There is no per-module selector — this is global, matching AgIO.
    /// AgIO layout (FormUDP.cs:19) with a computed CRC:
    /// { 0x80, 0x81, 0x7F, 201, 5, 201, 201, o1, o2, o3, CRC }.
    /// </summary>
    public static byte[] BuildSubnetChange(byte octet1, byte octet2, byte octet3)
        => WithCrc(new byte[] { HEADER1, HEADER2, SOURCE, PgnNumbers.SET_SUBNET, 5, 201, 201, octet1, octet2, octet3, 0 });

    /// <summary>
    /// Parse a PGN 203 scan reply (13 bytes): module id at [2], full module IP at
    /// [5..8], and at [9..11] the first three octets of the address the scan came
    /// from (AgOpenGPS firmware: <c>rem_ip</c>), which AgIO's Auto Set offers as the
    /// subnet to move the modules to. Some firmware (AiO v26) repeats the module's own
    /// subnet there instead. Returns false if not a well-formed scan reply. Pure so it
    /// can be unit-tested without sockets.
    /// </summary>
    public static bool TryParseScanReply(byte[] data, out byte moduleId, out string ip, out string scanSubnet)
    {
        moduleId = 0;
        ip = string.Empty;
        scanSubnet = string.Empty;

        if (data == null || data.Length < 12) return false;
        if (data[0] != HEADER1 || data[1] != HEADER2) return false;
        if (data[3] != PgnNumbers.SCAN_REPLY) return false;

        moduleId = data[2];
        ip = $"{data[5]}.{data[6]}.{data[7]}.{data[8]}";
        scanSubnet = $"{data[9]}.{data[10]}.{data[11]}";
        return true;
    }

    /// <summary>
    /// Validate a received PGN checksum using the same rule the send path uses:
    /// the trailing byte is the sum of bytes [2 .. len-2]. (This previously XOR'd
    /// bytes [0 .. len-2], which disagreed with every packet this class builds and
    /// would have rejected all valid traffic.)
    ///
    /// Diagnostics only — deliberately NOT wired into the receive path. Real
    /// modules ship packets carrying a placeholder CRC, so gating inbound handling
    /// on this would drop legitimate traffic and break communications.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ValidateChecksum(ReadOnlySpan<byte> data)
    {
        // header(2) + source + pgn + len + crc
        if (data.Length < 6) return false;

        int checksumPos = data.Length - 1;
        byte calculated = 0;
        for (int i = 2; i < checksumPos; i++)
        {
            calculated += data[i];
        }
        return calculated == data[checksumPos];
    }

    /// <summary>
    /// Build PGN 252 (Steer Settings) from AutoSteerConfig.
    /// Format: [0x80, 0x81, 0x7F, 0xFC, 8, gainP, highPWM, lowPWM, minPWM, countsPerDeg, offsetLo, offsetHi, ackerman, CRC]
    ///
    /// Byte 5:  Proportional gain (1-100)
    /// Byte 6:  High PWM limit (max PWM)
    /// Byte 7:  Low PWM limit (highPWM / 3)
    /// Byte 8:  Minimum PWM to move
    /// Byte 9:  Counts per degree (1-255)
    /// Byte 10: WAS offset low byte (little-endian)
    /// Byte 11: WAS offset high byte (little-endian)
    /// Byte 12: Ackermann correction (0-200)
    /// Byte 13: CRC
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte[] BuildSteerSettingsPgn(AutoSteerConfig config)
    {
        _steerSettingsBuffer ??= new byte[STEER_SETTINGS_PGN_SIZE];
        var buf = _steerSettingsBuffer;

        // Header
        buf[0] = HEADER1;
        buf[1] = HEADER2;
        buf[2] = SOURCE;
        buf[3] = PGN_STEER_SETTINGS;
        buf[4] = 8;  // Data length

        // Proportional gain (1-100)
        buf[5] = (byte)Math.Clamp(config.ProportionalGain, 1, 100);

        // High PWM (max PWM, 50-255)
        buf[6] = (byte)Math.Clamp(config.MaxPwm, 50, 255);

        // Low PWM (typically highPWM / 3)
        buf[7] = (byte)(buf[6] / 3);

        // Min PWM to move (1-50)
        buf[8] = (byte)Math.Clamp(config.MinPwm, 1, 50);

        // Counts per degree (1-255, sent as-is)
        // Round, not truncate: the CPD test can give 110.9, which must go out as 111 (#112).
        buf[9] = (byte)Math.Clamp((int)Math.Round(config.CountsPerDegree, MidpointRounding.AwayFromZero), 1, 255);

        // WAS offset (signed 16-bit, little-endian: low byte first)
        short wasOffset = (short)Math.Clamp(config.WasOffset, -32768, 32767);
        buf[10] = (byte)(wasOffset & 0xFF);        // low byte
        buf[11] = (byte)((wasOffset >> 8) & 0xFF); // high byte

        // Ackermann correction (0-200)
        buf[12] = (byte)Math.Clamp(config.Ackermann, 0, 200);

        return WithCrc(buf);
    }

    /// <summary>
    /// Build PGN 251 (Steer Config) from AutoSteerConfig.
    /// Format: [0x80, 0x81, 0x7F, 0xFB, 5, set0, pulseCount, minSpeed, set1, angVel, CRC]
    ///
    /// Byte 5 (set0):
    ///   bit 0: Invert WAS
    ///   bit 1: Invert Relays
    ///   bit 2: Invert Motor
    ///   bit 3: AD Converter (0=Differential, 1=Single)
    ///   bit 4: Motor Driver (0=IBT2, 1=Cytron)
    ///   bit 5-6: External Enable (0=None, 1=Switch, 2=Button)
    ///   bit 7: Turn Sensor enabled
    /// Byte 6:  Pulse count
    /// Byte 7:  Min steer speed * 10
    /// Byte 8 (set1):
    ///   bit 0: Danfoss
    ///   bit 1: Pressure Sensor
    ///   bit 2: Current Sensor
    ///   bit 3-4: IMU Axis Swap
    /// Byte 9:  Angular velocity
    /// Byte 10: CRC
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte[] BuildSteerConfigPgn(AutoSteerConfig config)
    {
        _steerConfigBuffer ??= new byte[STEER_CONFIG_PGN_SIZE];
        var buf = _steerConfigBuffer;

        // Header
        buf[0] = HEADER1;
        buf[1] = HEADER2;
        buf[2] = SOURCE;
        buf[3] = PGN_STEER_CONFIG;
        buf[4] = 5;  // Data length

        // Set0 byte (use helper from config)
        buf[5] = config.GetSetting0Byte();

        // Sensor kickout threshold (#105). The firmware (AiO v4 Autosteer.ino: PulseCountMax)
        // disengages when encoder pulses >= this, or when the pressure / current reading
        // (0-255) >= this. Hard-coding 0 made every enabled sensor kick out on every loop.
        buf[6] = SensorTripByte(config);

        // Min steer speed * 10 (AgOpenGPS sends it; standard firmware ignores this byte)
        buf[7] = (byte)Math.Clamp((int)(config.MinSteerSpeed * 10), 0, 255);

        // Set1 byte (use helper from config)
        buf[8] = config.GetSetting1Byte();

        // Angular velocity (not currently used, set to 0)
        buf[9] = 0;

        return WithCrc(buf);
    }

    /// <summary>
    /// PGN 251 byte 6, as AgOpenGPS FormSteer fills it: with a pressure or current sensor, that
    /// sensor's trip point (the panel's %, stored raw 0-255 — AgOpenGPS shows raw × 0.392 as %);
    /// otherwise the turn-sensor encoder count. A 0 % trip point means "off" (as the host-side
    /// kickout in AutoSteerService treats it, and it's the default) — sent as 255 so the
    /// firmware's <c>reading &gt;= threshold</c> doesn't trip on every loop.
    /// </summary>
    public static byte SensorTripByte(AutoSteerConfig config)
    {
        static byte Pct(int pct) => pct <= 0 ? (byte)255 : (byte)Math.Clamp((int)Math.Round(pct * 255.0 / 100.0), 1, 255);
        if (config.PressureSensorEnabled) return Pct(config.PressureTripPoint);
        if (config.CurrentSensorEnabled) return Pct(config.CurrentTripPoint);
        return (byte)Math.Clamp(config.TurnSensorCounts, 0, 255);
    }

    #region PGN 253 Parser (Steer Data FROM Module)

    /// <summary>
    /// Parse PGN 253 (Steer Data) received from the steering module.
    /// Format: [0x80, 0x81, Source, 0xFD, 8, AngleLo, AngleHi, HeadingLo, HeadingHi, RollLo, RollHi, Switches, PWM, CRC]
    ///
    /// Byte 5-6:  Actual steer angle * 100 (signed int16, little-endian)
    /// Byte 7-8:  Heading from IMU * 0.1 (signed int16, little-endian); 9999 = no IMU
    /// Byte 9-10: Roll from IMU * 0.1 (signed int16, little-endian); 8888 = no IMU
    /// Byte 11:   Switch status byte
    ///            bit 0: Work switch (INVERTED: 0=ON, 1=OFF)
    ///            bit 1: Steer switch active
    ///            bit 2: Remote steer button
    ///            bit 5: VWAS fusion active
    /// Byte 12:   PWM display (0-255)
    /// Byte 13:   CRC
    /// </summary>
    /// <param name="data">Raw PGN data including headers</param>
    /// <param name="result">Parsed steer module data</param>
    /// <returns>True if parsing succeeded, false if data is invalid</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryParseSteerData(ReadOnlySpan<byte> data, out SteerModuleData result)
    {
        result = default;

        // Validate minimum length: header(2) + source(1) + pgn(1) + length(1) + data(8) + crc(1) = 14
        if (data.Length < 14)
            return false;

        // Validate header
        if (data[0] != HEADER1 || data[1] != HEADER2)
            return false;

        // Validate PGN
        if (data[3] != PGN_STEER_DATA)
            return false;

        // Parse actual steer angle (signed int16, angle * 100)
        // Little-endian: low byte first (Arduino/Teensy convention)
        short angleRaw = (short)(data[5] | (data[6] << 8));
        double actualSteerAngle = angleRaw / 100.0;

        // Parse IMU heading (signed int16, heading * 0.1; 9999 = no IMU). AgOpenGPS uses it
        // when the GPS sentence carries no IMU heading. Little-endian: low byte first
        short headingRaw = (short)(data[7] | (data[8] << 8));
        double imuHeading = headingRaw * 0.1;
        bool hasImuHeading = headingRaw != 9999;

        // Parse IMU roll (signed int16, roll * 0.1; 8888 = no IMU). AgOpenGPS uses it
        // when the GPS sentence carries no roll (#353). Little-endian: low byte first
        short rollRaw = (short)(data[9] | (data[10] << 8));
        double imuRoll = rollRaw * 0.1;
        bool hasImuRoll = rollRaw != 8888;

        // Parse switch status byte (byte 11)
        // Bit 0: Work switch (INVERTED: 1=OFF, 0=ON)
        // Bit 1: Steer enabled
        // Bit 2: Remote/kickout button
        // Bit 5: VWAS fusion active
        byte switches = data[11];
        bool workSwitchActive = (switches & 0x01) == 0;  // Inverted logic!
        bool steerSwitchActive = (switches & 0x02) != 0;
        bool remoteButton = (switches & 0x04) != 0;
        bool vwasFusionActive = (switches & 0x20) != 0;

        // Parse PWM display (byte 12)
        byte pwmDisplay = data[12];

        result = new SteerModuleData(
            actualSteerAngle,
            imuHeading,
            imuRoll,
            workSwitchActive,
            steerSwitchActive,
            remoteButton,
            vwasFusionActive,
            pwmDisplay,
            hasImuRoll,
            hasImuHeading);

        return true;
    }

    /// <summary>
    /// Parse PGN 253 from a byte array (convenience overload).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryParseSteerData(byte[] data, out SteerModuleData result)
    {
        return TryParseSteerData(data.AsSpan(), out result);
    }

    #endregion

    #region PGN 211 Parser (External IMU)

    /// <summary>
    /// Parse PGN 211 (0xD3, external IMU) as AgOpenGPS does: 14 bytes, heading ×10 at
    /// bytes 5-6 and roll ×10 at bytes 7-8 (signed int16, little-endian). Roll is
    /// uncalibrated.
    /// </summary>
    public static bool TryParseImuData(ReadOnlySpan<byte> data, out double headingDegrees, out double rollDegrees)
    {
        headingDegrees = rollDegrees = 0;
        if (data.Length != 14 || data[0] != HEADER1 || data[1] != HEADER2 || data[3] != PgnNumbers.IMU_DATA)
            return false;
        headingDegrees = (short)(data[5] | (data[6] << 8)) * 0.1;
        rollDegrees = (short)(data[7] | (data[8] << 8)) * 0.1;
        return true;
    }

    #endregion

    #region PGN 250 Parser (Sensor Data FROM Module)

    /// <summary>
    /// Parse PGN 250 (Sensor Data) from raw bytes.
    /// Contains pressure/current sensor readings if hardware supports it.
    /// </summary>
    /// <param name="data">Raw PGN data including headers</param>
    /// <param name="result">Parsed sensor data</param>
    /// <returns>True if parsing succeeded, false if data is invalid</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryParseSensorData(ReadOnlySpan<byte> data, out SensorModuleData result)
    {
        result = default;

        // Validate minimum length: header(2) + source(1) + pgn(1) + length(1) + data(8) + crc(1) = 14
        if (data.Length < 14)
            return false;

        // Validate header
        if (data[0] != HEADER1 || data[1] != HEADER2)
            return false;

        // Validate PGN
        if (data[3] != PGN_SENSOR_DATA)
            return false;

        // Parse sensor value (byte 5)
        // This is the raw sensor reading - interpretation depends on hardware config
        // Could be pressure sensor, current sensor, or other diagnostic data
        byte sensorValue = data[5];

        result = new SensorModuleData(sensorValue);
        return true;
    }

    /// <summary>
    /// Parse PGN 250 from a byte array (convenience overload).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryParseSensorData(byte[] data, out SensorModuleData result)
    {
        return TryParseSensorData(data.AsSpan(), out result);
    }

    #endregion
}
