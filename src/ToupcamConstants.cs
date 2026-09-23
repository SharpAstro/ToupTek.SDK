namespace ToupTek.SDK;

/// <summary>
/// The constants this binding uses from <c>include/toupcam.h</c> (SDK 20260908), by their C names.
/// Only the ones read somewhere are here; the header has several hundred.
/// </summary>
/// <remarks>
/// Every rebadged library (<see cref="ToupcamBrand"/>) is the same SDK under another prefix, and the
/// values are shared: <c>ALTAIRCAM_OPTION_RAW</c> is <c>TOUPCAM_OPTION_RAW</c>. INDI's toupbase driver
/// relies on exactly that, building one source against each brand's header through a prefix macro.
/// </remarks>
internal static class ToupcamConstants
{
    // ---- HRESULT: >= 0 is success, and S_FALSE is a success too ("no change"), so never test == 0 ----

    internal const int S_OK = 0;
    internal const int S_FALSE = 1;
    internal const int E_UNEXPECTED = unchecked((int)0x8000FFFF);
    internal const int E_NOTIMPL = unchecked((int)0x80004001);
    internal const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
    internal const int E_INVALIDARG = unchecked((int)0x80070057);
    internal const int E_POINTER = unchecked((int)0x80004003);
    internal const int E_FAIL = unchecked((int)0x80004005);
    internal const int E_ACCESSDENIED = unchecked((int)0x80070005);
    internal const int E_WRONG_THREAD = unchecked((int)0x8001010E);
    internal const int E_GEN_FAILURE = unchecked((int)0x8007001F);
    internal const int E_BUSY = unchecked((int)0x800700AA);
    internal const int E_PENDING = unchecked((int)0x8000000A);
    internal const int E_TIMEOUT = unchecked((int)0x8001011F);

    /// <summary><c>TOUPCAM_MAX</c>: the enumeration array the SDK writes into holds this many entries.</summary>
    internal const int MaxDevices = 128;

    // ---- Options (Toupcam_put_Option / Toupcam_get_Option) ----

    internal const uint OPTION_RAW = 0x04;
    internal const uint OPTION_BITDEPTH = 0x06;
    internal const uint OPTION_FAN = 0x07;
    internal const uint OPTION_TEC = 0x08;
    internal const uint OPTION_TRIGGER = 0x0b;
    internal const uint OPTION_TECTARGET = 0x0f;
    internal const uint OPTION_BLACKLEVEL = 0x15;
    internal const uint OPTION_BINNING = 0x17;
    internal const uint OPTION_CG = 0x19;
    internal const uint OPTION_TEC_VOLTAGE = 0x20;
    internal const uint OPTION_TEC_VOLTAGE_MAX = 0x21;
    internal const uint OPTION_UPSIDE_DOWN = 0x23;
    internal const uint OPTION_HEAT_MAX = 0x36;
    internal const uint OPTION_HEAT = 0x37;
    internal const uint OPTION_TECTARGET_RANGE = 0x6d;
    internal const uint OPTION_ZERO_PADDING = 0x78;

    /// <summary><c>TOUPCAM_OPTION_BINNING</c> method bit: AVERAGE n*n, which keeps the bit depth and the
    /// ADU scale (a bare n is a SATURATING add, 0x40 | n an unsaturated one that widens the data).</summary>
    internal const int BINNING_AVERAGE = 0x80;

    /// <summary><c>TOUPCAM_OPTION_TRIGGER</c> value: software (simulated) trigger, one frame per
    /// <c>Toupcam_Trigger</c>.</summary>
    internal const int TRIGGER_SOFTWARE = 1;

    // ---- Model flags (ToupcamModelV2.flag, 64 bits) ----

    internal const ulong FLAG_MONO = 0x00000010;
    internal const ulong FLAG_USB30 = 0x00000040;
    internal const ulong FLAG_TEC = 0x00000080;
    internal const ulong FLAG_ST4 = 0x00000200;
    internal const ulong FLAG_GETTEMPERATURE = 0x00000400;
    internal const ulong FLAG_RAW10 = 0x00001000;
    internal const ulong FLAG_RAW12 = 0x00002000;
    internal const ulong FLAG_RAW14 = 0x00004000;
    internal const ulong FLAG_RAW16 = 0x00008000;
    internal const ulong FLAG_FAN = 0x00010000;
    internal const ulong FLAG_TEC_ONOFF = 0x00020000;
    internal const ulong FLAG_TRIGGER_SOFTWARE = 0x00080000;
    internal const ulong FLAG_BLACKLEVEL = 0x00400000;
    internal const ulong FLAG_RAW8 = 0x80000000;
    internal const ulong FLAG_CG = 0x04000000;
    internal const ulong FLAG_CGHDR = 0x0000000800000000;
    internal const ulong FLAG_HEAT = 0x0000008000000000;
    internal const ulong FLAG_RAW11 = 0x0080000000000000;
    internal const ulong FLAG_USB32 = 0x0400000000000000;

    // ---- Events delivered to the pull-mode callback ----

    internal const uint EVENT_IMAGE = 0x0004;
    internal const uint EVENT_TRIGGERFAIL = 0x0007;
    internal const uint EVENT_ERROR = 0x0080;
    internal const uint EVENT_DISCONNECTED = 0x0081;
    internal const uint EVENT_NOFRAMETIMEOUT = 0x0082;

    /// <summary><c>Toupcam_ST4PlusGuide</c> direction that stops a running pulse.</summary>
    internal const uint ST4_STOP = 4;

    internal static bool Succeeded(int hr) => hr >= 0;
}
