using System;
using System.Collections.Generic;

namespace ToupTek.SDK;

/// <summary>
/// One vendor library in the ToupTek family: the same SDK, rebadged under another library name and
/// function prefix, each enumerating only that brand's cameras.
/// </summary>
/// <remarks>
/// <para><b>The table is INDI's, not recalled.</b> Names and prefixes are taken from
/// <c>indi-3rdparty/indi-toupbase/libtoupbase.h</c>, which builds one driver source against each
/// brand's header through a <c>FP(x)</c> prefix macro, and from the matching <c>lib*</c> folders of
/// that repository. INDIGO ships the ToupTek library the same way (<c>ccd_touptek/bin_externals</c>).
/// Only <see cref="ToupTek"/> is verified on hardware here, on a G3M678M; the others are
/// expected to work where their library is installed and are unverified until a body is plugged in.</para>
/// <para><b>The prefix is not always the library's own name.</b> Meade's <c>meadecam</c> exports
/// <c>Toupcam_</c> symbols, which is why every entry point is resolved against its OWN library handle
/// (<see cref="ToupcamApi.TryLoad"/>) and never by global name: loading <c>toupcam</c> and
/// <c>meadecam</c> side by side is then two independent tables that cannot shadow each other.</para>
/// </remarks>
public sealed class ToupcamBrand
{
    private readonly Lazy<ToupcamApi?> _api;

    private ToupcamBrand(string name, string libraryName, string prefix)
    {
        Name = name;
        LibraryName = libraryName;
        Prefix = prefix;
        _api = new Lazy<ToupcamApi?>(() => ToupcamApi.TryLoad(this));
    }

    /// <summary>What the brand is called, for display and device identity.</summary>
    public string Name { get; }

    /// <summary>The native library, without platform decoration (<c>toupcam</c> is <c>toupcam.dll</c>,
    /// <c>libtoupcam.so</c>, <c>libtoupcam.dylib</c>).</summary>
    public string LibraryName { get; }

    /// <summary>The function prefix, without its underscore (<c>Toupcam</c> for <c>Toupcam_Open</c>).</summary>
    public string Prefix { get; }

    /// <summary>The loaded entry points, or null when this brand's library is not installed (the
    /// common case for all but one or two of them) or lacks a function this binding needs.</summary>
    public ToupcamApi? Api => _api.Value;

    public static readonly ToupcamBrand ToupTek = new ToupcamBrand("ToupTek", "toupcam", "Toupcam");
    public static readonly ToupcamBrand Altair = new ToupcamBrand("Altair", "altaircam", "Altaircam");
    public static readonly ToupcamBrand Bresser = new ToupcamBrand("Bresser", "bressercam", "Bressercam");
    public static readonly ToupcamBrand MallinCam = new ToupcamBrand("MallinCam", "mallincam", "Mallincam");
    public static readonly ToupcamBrand Nn = new ToupcamBrand("Nn", "nncam", "Nncam");
    public static readonly ToupcamBrand OgmaVision = new ToupcamBrand("OGMAVision", "ogmacam", "Ogmacam");
    public static readonly ToupcamBrand Omegon = new ToupcamBrand("Omegon", "omegonprocam", "Omegonprocam");
    public static readonly ToupcamBrand Orion = new ToupcamBrand("Orion", "starshootg", "Starshootg");
    public static readonly ToupcamBrand TeleskopService = new ToupcamBrand("TeleskopService", "tscam", "Tscam");
    public static readonly ToupcamBrand Svbony = new ToupcamBrand("SVBONY", "svbonycam", "Svbonycam");
    public static readonly ToupcamBrand Meade = new ToupcamBrand("Meade", "meadecam", "Toupcam");

    /// <summary>Every brand, ToupTek first.</summary>
    public static IReadOnlyList<ToupcamBrand> All { get; } =
        [ToupTek, Altair, Bresser, MallinCam, Nn, OgmaVision, Omegon, Orion, TeleskopService, Svbony, Meade];

    public override string ToString() => $"{Name} ({LibraryName}, {Prefix}_)";
}
