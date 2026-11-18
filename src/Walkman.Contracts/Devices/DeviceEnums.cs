namespace Walkman.Contracts.Devices;

/// <summary>
/// Sony Network Walkman / ATRAC hardware generations across 1999-2010.
/// </summary>
public enum WalkmanGeneration
{
    Gen0_EarlyOpenMg = 0,
    Gen1_EarlyFlash = 1,
    Gen2_Transition = 2,
    Gen3_OmgV1 = 3,
    Gen4_OmgV2_Connect = 4,
    Gen5_FlashStick = 5,
    Gen6_Sports = 6,
    Gen7_ColorScreen = 7,
    AtracCd = 8
}

/// <summary>
/// Physical transport protocol used to communicate with a player.
/// </summary>
public enum TransportProtocol
{
    MassStorage,
    LegacyNetUsb,
    OpticalIso9660
}

/// <summary>
/// Binary database index format stored on the player.
/// </summary>
public enum DatabaseFormat
{
    OmgV1,
    OmgV2,
    Mp3Fm,
    HifiMsf,
    AtracCdMsf
}

/// <summary>
/// Sony EA3 descriptor byte 0x20 codec identifier.
/// </summary>
public enum AudioCodecId : byte
{
    Atrac3 = 0x00,
    Atrac3Plus = 0x01,
    Aac = 0x02,
    Mp3 = 0x03,
    LinearPcm = 0x04,
    Wma = 0x05,
    Atrac3PlusLossless = 0x21,
    Atrac3Lossless = 0x22
}

/// <summary>
/// Sony EA3 descriptor bytes 0x06..0x07 encryption identifier (Big-Endian).
/// </summary>
public enum OmaEncryptionMode : ushort
{
    MagicGateLsi_0x0001 = 0x0001,
    HardwareXor_0xFFFE = 0xFFFE,
    Unprotected_0xFFFF = 0xFFFF
}

/// <summary>
/// Sony .nwm screensaver container magic header (uint16 Big-Endian).
/// </summary>
public enum ScreensaverMagic : ushort
{
    None = 0x0000,
    NwE400_0xEC01 = 0xEC01,
    NwE002_0xEC02 = 0xEC02,
    NwA1000_0xD301 = 0xD301
}
