// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// AgOpenGPS's outgoing frames, ported line for line from the snapshot in <c>AgOpen_Snapshot/</c>
/// (2026-01-24): the <c>CPGN_*</c> layouts in <c>GPS/Forms/PGN.Designer.cs</c>, the PGN 254 fill in
/// <c>Position.designer.cs</c>, the section bytes in <c>Sections.Designer.cs</c>, PGN 239's bytes
/// from <c>OpenGL.Designer.cs</c> / <c>CHead.cs</c> / <c>Sections.Designer.cs</c>, PGN 252 and 251
/// from <c>FormGPS.SendSettings</c>, the 251 bits from <c>FormSteer.SaveSettings</c>, and the CRC from
/// <c>UDPComm.Designer.cs SendPgnToLoop</c>. It is the oracle the parity tests compare
/// <see cref="AutoSteer.PgnBuilder"/>'s frames with, byte for byte, so it keeps AgOpenGPS's names,
/// order and arithmetic. Don't tidy it: a difference from the snapshot is a difference from AgOpenGPS.
/// </summary>
internal static class AgOpenGpsPgnOracle
{
    // UDPComm.Designer.cs SendPgnToLoop: the CRC is the byte sum from the source byte to the last data byte.
    private static byte[] Sent(byte[] byteData)
    {
        int crc = 0;
        for (int i = 2; i + 1 < byteData.Length; i++)
        {
            crc += byteData[i];
        }
        byteData[byteData.Length - 1] = (byte)crc;
        return byteData;
    }

    /// <summary>
    /// PGN 254 (CPGN_FE), filled as Position.designer.cs does each GPS cycle. The recorded-path,
    /// changing-direction and dead-zone cases are left out (they only hold or force the status
    /// and the angle bytes); the section bytes are Sections.Designer.cs's.
    /// </summary>
    public static byte[] SteerData(double avgSpeed, bool isBtnAutoSteerOn, short guidanceLineDistanceOff,
        short guidanceLineSteerAngle, bool isReverse, bool isSteerInReverse,
        bool isInFreeDriveMode, double driveFreeSteerAngle, byte sc1to8, byte sc9to16)
    {
        byte[] pgn = { 0x80, 0x81, 0x7f, 0xFE, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0xCC };
        const int speedLo = 5, speedHi = 6, status = 7, steerAngleLo = 8, steerAngleHi = 9, lineDistance = 10;

        if (!isInFreeDriveMode)
        {
            pgn[speedHi] = unchecked((byte)((int)(Math.Abs(avgSpeed) * 10.0) >> 8));
            pgn[speedLo] = unchecked((byte)((int)(Math.Abs(avgSpeed) * 10.0)));

            if (!isBtnAutoSteerOn) //32020 means auto steer is off
            {
                guidanceLineDistanceOff = 32020;
                pgn[status] = 0;
            }

            else pgn[status] = 1;

            //convert to cm from mm and divide by 2 - lightbar
            int distanceX2;
            if (guidanceLineDistanceOff == 32020 || guidanceLineDistanceOff == 32000)
                distanceX2 = 255;

            else
            {
                distanceX2 = (int)(guidanceLineDistanceOff * 0.05);

                if (distanceX2 < -127) distanceX2 = -127;
                else if (distanceX2 > 127) distanceX2 = 127;
                distanceX2 += 127;
            }

            pgn[lineDistance] = unchecked((byte)distanceX2);

            //for now if backing up, turn off autosteer
            if (!isSteerInReverse)
            {
                if (isReverse) pgn[status] = 0;
            }

            pgn[steerAngleHi] = unchecked((byte)(guidanceLineSteerAngle >> 8));
            pgn[steerAngleLo] = unchecked((byte)(guidanceLineSteerAngle));
        }

        else //Drive button is on
        {
            pgn[speedHi] = unchecked((byte)((int)(80) >> 8));
            pgn[speedLo] = unchecked((byte)((int)(80)));

            //turn on status to operate
            pgn[status] = 1;

            guidanceLineSteerAngle = (Int16)(driveFreeSteerAngle * 100);

            pgn[steerAngleHi] = unchecked((byte)(guidanceLineSteerAngle >> 8));
            pgn[steerAngleLo] = unchecked((byte)(guidanceLineSteerAngle));
        }

        pgn[11] = sc1to8;
        pgn[12] = sc9to16;
        return Sent(pgn);
    }

    /// <summary>
    /// PGN 239 (CPGN_EF): uturn from OpenGL.Designer.cs (1 while a turn is triggered), speed and
    /// tram from Sections.Designer.cs, hydLift from CHead.SetHydPosition (0 off, 1 down, 2 up),
    /// geoStop from OpenGL.Designer.cs, the section bytes copied from PGN 254's.
    /// </summary>
    public static byte[] MachineData(bool isYouTurnTriggered, double avgSpeed, byte hydLift, byte tramControlByte,
        bool isOutOfBounds, byte sc1to8, byte sc9to16)
    {
        byte[] pgn = { 0x80, 0x81, 0x7f, 0xEF, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0xCC };
        const int uturn = 5, speed = 6, hydLiftI = 7, tram = 8, geoStop = 9;

        pgn[uturn] = isYouTurnTriggered ? (byte)1 : (byte)0;
        pgn[speed] = unchecked((byte)(avgSpeed * 10));
        pgn[hydLiftI] = hydLift;
        pgn[tram] = unchecked((byte)tramControlByte);
        pgn[geoStop] = isOutOfBounds ? (byte)1 : (byte)0;
        pgn[11] = sc1to8;
        pgn[12] = sc9to16;
        return Sent(pgn);
    }

    /// <summary>PGN 252 (CPGN_FC) as FormGPS.SendSettings fills it from the settings.</summary>
    public static byte[] SteerSettings(int setAS_countsPerDegree, int setAS_ackerman, int setAS_wasOffset,
        int setAS_highSteerPWM, int setAS_lowSteerPWM, int setAS_Kp, int setAS_minSteerPWM)
    {
        byte[] pgn = { 0x80, 0x81, 0x7f, 0xFC, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0xCC };
        const int gainProportional = 5, highPWM = 6, lowPWM = 7, minPWM = 8, countsPerDegree = 9, wasOffsetLo = 10, wasOffsetHi = 11, ackerman = 12;

        pgn[countsPerDegree] = unchecked((byte)setAS_countsPerDegree);
        pgn[ackerman] = unchecked((byte)setAS_ackerman);

        pgn[wasOffsetHi] = unchecked((byte)(setAS_wasOffset >> 8));
        pgn[wasOffsetLo] = unchecked((byte)(setAS_wasOffset));

        pgn[highPWM] = unchecked((byte)setAS_highSteerPWM);
        pgn[lowPWM] = unchecked((byte)setAS_lowSteerPWM);
        pgn[gainProportional] = unchecked((byte)setAS_Kp);
        pgn[minPWM] = unchecked((byte)setAS_minSteerPWM);
        return Sent(pgn);
    }

    /// <summary>FormSteer stores the low PWM as a third of the high.</summary>
    public static int LowSteerPWM(int hsbarHighSteerPWM) => unchecked((byte)(hsbarHighSteerPWM / 3));

    /// <summary>PGN 251 (CPGN_FB) as FormGPS.SendSettings fills it from the settings.</summary>
    public static byte[] SteerConfig(byte setArdSteer_setting0, byte setArdSteer_setting1, byte setArdSteer_maxPulseCounts,
        double setAS_minSteerSpeed, bool setAS_isConstantContourOn)
    {
        byte[] pgn = { 0x80, 0x81, 0x7f, 0xFB, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0xCC };
        const int set0 = 5, maxPulse = 6, minSpeed = 7, set1 = 8, angVel = 9;

        pgn[set0] = setArdSteer_setting0;
        pgn[set1] = setArdSteer_setting1;
        pgn[maxPulse] = setArdSteer_maxPulseCounts;
        pgn[minSpeed] = unchecked((byte)(setAS_minSteerSpeed * 10));

        if (setAS_isConstantContourOn)
            pgn[angVel] = 1;
        else pgn[angVel] = 0;
        return Sent(pgn);
    }

    /// <summary>setArdSteer_setting0 as FormSteer.SaveSettings assembles it from the form.</summary>
    public static byte Setting0(bool chkInvertWAS, bool chkSteerInvertRelays, bool chkInvertSteer,
        string cboxConv, string cboxMotorDrive, string cboxSteerEnable, bool cboxEncoder)
    {
        int set = 1;
        int reset = 2046;
        int sett = 0;

        if (chkInvertWAS) sett |= set;
        else sett &= reset;

        set <<= 1;
        reset <<= 1;
        reset += 1;

        if (chkSteerInvertRelays) sett |= set;
        else sett &= reset;

        set <<= 1;
        reset <<= 1;
        reset += 1;

        if (chkInvertSteer) sett |= set;
        else sett &= reset;

        set <<= 1;
        reset <<= 1;
        reset += 1;

        if (cboxConv == "Single") sett |= set;
        else sett &= reset;

        set <<= 1;
        reset <<= 1;
        reset += 1;

        if (cboxMotorDrive == "Cytron") sett |= set;
        else sett &= reset;

        set <<= 1;
        reset <<= 1;
        reset += 1;

        if (cboxSteerEnable == "Switch") sett |= set;
        else sett &= reset;

        set <<= 1;
        reset <<= 1;
        reset += 1;

        if (cboxSteerEnable == "Button") sett |= set;
        else sett &= reset;

        set <<= 1;
        reset <<= 1;
        reset += 1;

        if (cboxEncoder) sett |= set;
        else sett &= reset;

        return (byte)sett;
    }

    /// <summary>setArdSteer_maxPulseCounts as FormSteer.SaveSettings stores it.</summary>
    public static byte MaxPulseCounts(bool cboxCurrentSensor, bool cboxPressureSensor, int hsbarSensor, int nudMaxCounts)
    {
        if (cboxCurrentSensor || cboxPressureSensor)
            return (byte)hsbarSensor;
        else
            return (byte)nudMaxCounts;
    }

    /// <summary>setArdSteer_setting1 as FormSteer.SaveSettings assembles it from the form.</summary>
    public static byte Setting1(bool cboxDanfoss, bool cboxPressureSensor, bool cboxCurrentSensor, string cboxXY)
    {
        int set = 1;
        int reset = 2046;
        int sett = 0;

        if (cboxDanfoss) sett |= set;
        else sett &= reset;

        set <<= 1;
        reset <<= 1;
        reset += 1;

        if (cboxPressureSensor) sett |= set;
        else sett &= reset;

        //bit 2
        set <<= 1;
        reset <<= 1;
        reset += 1;

        if (cboxCurrentSensor) sett |= set;
        else sett &= reset;

        //bit 3
        set <<= 1;
        reset <<= 1;
        reset += 1;

        if (cboxXY == "Y") sett |= set;
        else sett &= reset;

        return (byte)sett;
    }
}
