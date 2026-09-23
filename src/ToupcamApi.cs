using System;
using System.Runtime.InteropServices;

namespace ToupTek.SDK;

/// <summary>
/// One brand's entry points, resolved at run time from that brand's own library.
/// </summary>
/// <remarks>
/// <para><b>Why function pointers and not <c>[LibraryImport]</c>.</b> An import's library and entry
/// point are compile-time constants, and the family is eleven libraries exporting the same API under
/// eleven prefixes (<see cref="ToupcamBrand"/>). Eleven copies of every import is the only static
/// alternative. Unmanaged function pointers resolved through <see cref="NativeLibrary.GetExport"/> are
/// one table, AOT-clean (no delegate marshalling, no reflection), and cost one indirect call.</para>
/// <para><b>Calling convention.</b> <c>TOUPCAM_API</c> is <c>__stdcall</c> on Windows, which only
/// matters on x86; everywhere else <c>Stdcall</c> is ignored and the platform default applies, which is
/// what the Unix builds use. The vendor's own C# binding says <c>CallingConvention.Winapi</c> for the
/// same reason.</para>
/// <para><b>Strings are the platform's own.</b> On Windows every string argument and field is
/// <c>wchar_t</c> (UTF-16); elsewhere it is <c>char</c>. The pointers below are therefore untyped, and
/// <see cref="ToupcamNativeText"/> does the conversion in one place.</para>
/// </remarks>
public sealed unsafe class ToupcamApi
{
    internal readonly nint Library;

    internal readonly delegate* unmanaged[Stdcall]<nint> Version;
    internal readonly delegate* unmanaged[Stdcall]<void*, uint> EnumV2;
    internal readonly delegate* unmanaged[Stdcall]<void*, nint> Open;
    internal readonly delegate* unmanaged[Stdcall]<nint, void> Close;
    internal readonly delegate* unmanaged[Stdcall]<nint, delegate* unmanaged[Stdcall]<uint, nint, void>, nint, int> StartPullModeWithCallback;
    internal readonly delegate* unmanaged[Stdcall]<nint, int> Stop;
    internal readonly delegate* unmanaged[Stdcall]<nint, void*, int, int, int, void*, int> PullImageV4;
    internal readonly delegate* unmanaged[Stdcall]<nint, ushort, int> Trigger;
    internal readonly delegate* unmanaged[Stdcall]<nint, uint, int> PutExpoTime;
    internal readonly delegate* unmanaged[Stdcall]<nint, uint*, int> GetExpoTime;
    internal readonly delegate* unmanaged[Stdcall]<nint, uint*, uint*, uint*, int> GetExpTimeRange;
    internal readonly delegate* unmanaged[Stdcall]<nint, ushort, int> PutExpoAGain;
    internal readonly delegate* unmanaged[Stdcall]<nint, ushort*, int> GetExpoAGain;
    internal readonly delegate* unmanaged[Stdcall]<nint, ushort*, ushort*, ushort*, int> GetExpoAGainRange;
    internal readonly delegate* unmanaged[Stdcall]<nint, int, int> PutAutoExpoEnable;
    internal readonly delegate* unmanaged[Stdcall]<nint, uint, int, int> PutOption;
    internal readonly delegate* unmanaged[Stdcall]<nint, uint, int*, int> GetOption;
    internal readonly delegate* unmanaged[Stdcall]<nint, short*, int> GetTemperature;
    internal readonly delegate* unmanaged[Stdcall]<nint, byte*, int> GetSerialNumber;
    internal readonly delegate* unmanaged[Stdcall]<nint, byte*, int> GetFwVersion;
    internal readonly delegate* unmanaged[Stdcall]<nint, uint*, uint*, int> GetRawFormat;
    internal readonly delegate* unmanaged[Stdcall]<nint, uint, uint, uint, uint, int> PutRoi;
    internal readonly delegate* unmanaged[Stdcall]<nint, uint*, uint*, uint*, uint*, int> GetRoi;
    internal readonly delegate* unmanaged[Stdcall]<nint, int*, int*, int> GetFinalSize;
    internal readonly delegate* unmanaged[Stdcall]<nint, int> GetMaxBitDepth;
    internal readonly delegate* unmanaged[Stdcall]<nint, uint, uint, int> ST4PlusGuide;

    private ToupcamApi(nint library, string prefix)
    {
        Library = library;
        Version = (delegate* unmanaged[Stdcall]<nint>)Export(library, prefix, "Version");
        EnumV2 = (delegate* unmanaged[Stdcall]<void*, uint>)Export(library, prefix, "EnumV2");
        Open = (delegate* unmanaged[Stdcall]<void*, nint>)Export(library, prefix, "Open");
        Close = (delegate* unmanaged[Stdcall]<nint, void>)Export(library, prefix, "Close");
        StartPullModeWithCallback = (delegate* unmanaged[Stdcall]<nint, delegate* unmanaged[Stdcall]<uint, nint, void>, nint, int>)Export(library, prefix, "StartPullModeWithCallback");
        Stop = (delegate* unmanaged[Stdcall]<nint, int>)Export(library, prefix, "Stop");
        PullImageV4 = (delegate* unmanaged[Stdcall]<nint, void*, int, int, int, void*, int>)Export(library, prefix, "PullImageV4");
        Trigger = (delegate* unmanaged[Stdcall]<nint, ushort, int>)Export(library, prefix, "Trigger");
        PutExpoTime = (delegate* unmanaged[Stdcall]<nint, uint, int>)Export(library, prefix, "put_ExpoTime");
        GetExpoTime = (delegate* unmanaged[Stdcall]<nint, uint*, int>)Export(library, prefix, "get_ExpoTime");
        GetExpTimeRange = (delegate* unmanaged[Stdcall]<nint, uint*, uint*, uint*, int>)Export(library, prefix, "get_ExpTimeRange");
        PutExpoAGain = (delegate* unmanaged[Stdcall]<nint, ushort, int>)Export(library, prefix, "put_ExpoAGain");
        GetExpoAGain = (delegate* unmanaged[Stdcall]<nint, ushort*, int>)Export(library, prefix, "get_ExpoAGain");
        GetExpoAGainRange = (delegate* unmanaged[Stdcall]<nint, ushort*, ushort*, ushort*, int>)Export(library, prefix, "get_ExpoAGainRange");
        PutAutoExpoEnable = (delegate* unmanaged[Stdcall]<nint, int, int>)Export(library, prefix, "put_AutoExpoEnable");
        PutOption = (delegate* unmanaged[Stdcall]<nint, uint, int, int>)Export(library, prefix, "put_Option");
        GetOption = (delegate* unmanaged[Stdcall]<nint, uint, int*, int>)Export(library, prefix, "get_Option");
        GetTemperature = (delegate* unmanaged[Stdcall]<nint, short*, int>)Export(library, prefix, "get_Temperature");
        GetSerialNumber = (delegate* unmanaged[Stdcall]<nint, byte*, int>)Export(library, prefix, "get_SerialNumber");
        GetFwVersion = (delegate* unmanaged[Stdcall]<nint, byte*, int>)Export(library, prefix, "get_FwVersion");
        GetRawFormat = (delegate* unmanaged[Stdcall]<nint, uint*, uint*, int>)Export(library, prefix, "get_RawFormat");
        PutRoi = (delegate* unmanaged[Stdcall]<nint, uint, uint, uint, uint, int>)Export(library, prefix, "put_Roi");
        GetRoi = (delegate* unmanaged[Stdcall]<nint, uint*, uint*, uint*, uint*, int>)Export(library, prefix, "get_Roi");
        GetFinalSize = (delegate* unmanaged[Stdcall]<nint, int*, int*, int>)Export(library, prefix, "get_FinalSize");
        GetMaxBitDepth = (delegate* unmanaged[Stdcall]<nint, int>)Export(library, prefix, "get_MaxBitDepth");
        ST4PlusGuide = (delegate* unmanaged[Stdcall]<nint, uint, uint, int>)Export(library, prefix, "ST4PlusGuide");
    }

    /// <summary>
    /// Loads a brand's library and every entry point this binding needs, or null.
    /// </summary>
    /// <remarks>
    /// Null for two different reasons, both normal: the library is not installed (ten of the eleven
    /// brands, on most machines), or it is an older build missing a function used here. Either way the
    /// brand simply offers no cameras, which is what a caller would see with no camera plugged in; a
    /// half-resolved table that fails at the first missing call would be worse. The library handle is
    /// released on the second path, so a failed probe leaves nothing mapped.
    /// </remarks>
    internal static ToupcamApi? TryLoad(ToupcamBrand brand)
    {
        if (!NativeLibrary.TryLoad(brand.LibraryName, typeof(ToupcamApi).Assembly, null, out var library))
        {
            return null;
        }

        try
        {
            return new ToupcamApi(library, brand.Prefix);
        }
        catch (EntryPointNotFoundException)
        {
            NativeLibrary.Free(library);
            return null;
        }
    }

    /// <summary>The SDK's version string, e.g. <c>60.32549.20260908</c>.</summary>
    public string SdkVersion => ToupcamNativeText.FromPointer(Version());

    private static nint Export(nint library, string prefix, string name)
        => NativeLibrary.GetExport(library, $"{prefix}_{name}");
}
