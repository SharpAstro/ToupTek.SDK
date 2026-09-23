using System;
using System.Collections.Generic;
using TianWen.DAL;
using static ToupTek.SDK.ToupcamConstants;

namespace ToupTek.SDK;

/// <summary>
/// A ToupTek-family camera as the DAL sees it.
/// </summary>
/// <remarks>
/// <para>A value type because the DAL contract requires one (see <see cref="ToupcamSession"/> for why
/// the live state lives elsewhere). Everything here is either a fact copied at enumeration
/// (<see cref="ToupcamModel"/>) or a call through the session the key names; a copy of this struct taken
/// before <see cref="Open"/> works after it.</para>
/// <para><b>What the DAL's controls mean on this SDK</b>, since the names are shared across vendors:
/// <list type="bullet">
/// <item><c>Exposure</c> is microseconds (<c>Toupcam_put_ExpoTime</c>).</item>
/// <item><c>Gain</c> is the SDK's analogue gain in PERCENT, 100 being unity (<c>put_ExpoAGain</c>), so
/// its numbers are not comparable with a ZWO or Player One gain.</item>
/// <item><c>Brightness</c> is the black level (<c>TOUPCAM_OPTION_BLACKLEVEL</c>), the role the other
/// bindings give it. Its range scales with the CURRENT bit depth: 0 to 31 at 8 bits, 31 x 2^(bits-8)
/// above that.</item>
/// <item><c>TemperatureDeci</c> is <c>get_Temperature</c>, already tenths of a degree;
/// <c>TargetTemperature</c> is whole degrees over <c>TOUPCAM_OPTION_TECTARGET</c>'s tenths.</item>
/// </list></para>
/// <para><b>Conversion gain is deliberately not touched.</b> <c>TOUPCAM_OPTION_CG</c> (LCG, HCG and, on
/// some bodies, HDR) stays at the camera's own default: SharpCap likewise keeps it behind an opt-in
/// "Read Mode" control, and HDR mode on this family has depended on matching firmware and SDK versions.
/// The DAL has no read-mode control yet, which is where exposing it belongs.</para>
/// </remarks>
public readonly struct ToupcamCamera : ICMOSNativeInterface
{
    /// <summary>
    /// Sensor dies stated per model, only where the model is known to carry that die. The DAL's own
    /// <see cref="SensorModelNames"/> knows ZWO and QHY product names, not ToupTek's, and a wrong
    /// alias is worse than a missing one: it applies a confidently incorrect QE to a colour calibration.
    /// </summary>
    private static readonly Dictionary<string, string> SensorByModel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["G3M678M"] = "IMX678",
    };

    /// <summary>What a DEFAULT instance reports: the DAL hands back <c>default</c> when a connect finds no
    /// matching camera, and nothing about that instance may throw.</summary>
    private static readonly ToupcamModel NoModel = new ToupcamModel("", 0, 0, 0, 0, 0, []);

    private readonly ToupcamDeviceEntry? _entry;
    private readonly string? _key;

    internal ToupcamCamera(ToupcamDeviceEntry entry)
    {
        _entry = entry;
        _key = ToupcamSession.KeyOf(entry);
    }

    /// <summary>The library this camera was enumerated through, or null on a default instance.</summary>
    public ToupcamBrand? Brand => _entry?.Brand;

    /// <summary>What the SDK says the model is.</summary>
    public ToupcamModel Model => _entry?.Model ?? NoModel;

    /// <summary>The camera firmware, once open; null before.</summary>
    public string? FirmwareVersion => Session?.FirmwareVersion;

    /// <summary>The conversion gain mode the camera is in (0 LCG, 1 HCG, 2 HDR), or null when closed or
    /// when the body has none. Read-only here: see the type's remarks for why it is not set.</summary>
    public int? ConversionGain => Session?.ConversionGain();

    /// <summary>What <c>Toupcam_get_RawFormat</c> reports as bits per pixel in the current mode, or null
    /// when closed.</summary>
    public int? RawBitsPerPixel => Session?.RawBitsPerPixel();

    private ToupcamSession? Session => _key is null ? null : ToupcamSession.Find(_key);

    // ---- Identity -------------------------------------------------------------------------------

    public int ID => _entry?.Index ?? -1;

    public string Name => Model.Name;

    /// <summary>The SDK's display name, which is the model name unless the user set one.</summary>
    public string CustomId => _entry?.DisplayName is { Length: > 0 } display ? display : Name;

    /// <summary>The factory serial, which needs an OPEN camera here (<c>Toupcam_get_SerialNumber</c>
    /// takes a handle); null before <see cref="Open"/> or when the body has none.</summary>
    public string? SerialNumber => Session?.SerialNumber;

    public bool IsUSB3Device => Model.Has(FLAG_USB30) || Model.Has(FLAG_USB32);

    public string? SensorModel
        => SensorByModel.TryGetValue(Name, out var die) ? die
            : SensorModelNames.TryGetSensorModel(Name, out var known) ? known
            : null;

    public bool Open() => _entry is not null && ToupcamSession.Acquire(_entry);

    public bool Close() => _key is not null && ToupcamSession.Release(_key);

    /// <summary>True whenever the library is loaded: <c>TOUPCAM_OPTION_DEVICE_RESET</c> is a general
    /// option of the SDK, not a model capability.</summary>
    public bool CanResetDevice => _entry?.Brand.Api is not null;

    /// <summary>
    /// Resets the camera as a replug would. The camera must be open (the reset is issued through its
    /// handle), and afterwards it is CLOSED and at its power-on defaults, so the caller enumerates and
    /// opens it again; see <see cref="INativeDeviceInfo.ResetDevice"/>.
    /// </summary>
    public CMOSErrorCode ResetDevice()
        => _key is not null && Session is not null ? ToDALError(ToupcamSession.Reset(_key)) : CMOSErrorCode.CameraClosed;

    // ---- What the sensor is ---------------------------------------------------------------------

    public int MaxWidth => Model.Resolutions.Length > 0 ? Model.Resolutions[0].Width : 0;

    public int MaxHeight => Model.Resolutions.Length > 0 ? Model.Resolutions[0].Height : 0;

    /// <summary>The converter depth: the open handle's own answer, else the highest RAW flag the
    /// model declares.</summary>
    public int BitDepth => Session?.MaxBitDepth ?? DepthFromFlags(Model);

    /// <summary>
    /// False, but only because <see cref="BitDepth"/> is the SDK's own 16: relative to the depth this
    /// binding declares there is no shift, and the declared saturation (65535) is right.
    /// </summary>
    /// <remarks>
    /// <para><b>Measured, and the header comment is not what it looks like.</b> On the G3M678M (IMX678,
    /// firmware 4.0.2.202300708, SDK 60.32549.20260908) in its default HCG mode
    /// (<c>TOUPCAM_OPTION_CG = 1</c>), <c>get_MaxBitDepth</c> and <c>get_RawFormat</c> both say 16, the
    /// model sets <c>TOUPCAM_FLAG_RAW16</c> and not RAW12, and the pixels are the sensor's 12-bit
    /// converter LEFT-ALIGNED: 8,293,901 of 8,294,400 values a multiple of 16 (the other 0.006 percent
    /// consistent with on-camera defect correction interpolating), and a 200 ms exposure clipping at
    /// exactly 65520 = 4095 x 16. That is with <c>TOUPCAM_OPTION_ZERO_PADDING = 0</c>, whose header text
    /// ("0 => high") reads as native alignment and is not. Average binning divides the step by the bin
    /// squared (4 at bin 2), as it does on Player One.</para>
    /// <para>Declaring 12 bits here with this flag true would state the converter more precisely and
    /// declare the same 65535; it is not done because the SDK offers nothing that says 12 (the body
    /// also has an HDR conversion-gain mode, <c>TOUPCAM_FLAG_CGHDR</c>, whose output may be genuinely
    /// 16-bit), and a depth guessed from the sensor name is the kind of alias that is worse wrong than
    /// missing. A consumer that needs the quantum measures it per frame, which is the rule for archive
    /// files anyway.</para>
    /// </remarks>
    public bool DeliversContainerScaledPixels => false;

    public double PixelSize => Model.XPixelSize;

    public BayerPattern BayerPattern
    {
        get
        {
            if (Model.Has(FLAG_MONO))
            {
                return BayerPattern.Monochrome;
            }

            // A colour body's mosaic is asked of the open camera; there is no guess to fall back on
            // that could not be wrong, and a wrong one swaps red and blue without any error.
            if (Session is not { } session)
            {
                throw new InvalidOperationException($"{Name}: the Bayer pattern of a colour body is read from the open camera");
            }

            return session.RawFourCc() switch
            {
                var cc when cc == FourCc('R', 'G', 'G', 'B') => BayerPattern.RGGB,
                var cc when cc == FourCc('B', 'G', 'G', 'R') => BayerPattern.BGGR,
                var cc when cc == FourCc('G', 'R', 'B', 'G') => BayerPattern.GRBG,
                var cc when cc == FourCc('G', 'B', 'R', 'G') => BayerPattern.GBRG,
                var cc => throw new NotSupportedException($"{Name}: unsupported raw format 0x{cc:X8}"),
            };
        }
    }

    /// <summary>Digital binning by AVERAGE (<c>0x80 | n</c>), which keeps the bit depth and the ADU
    /// scale, so a binned frame calibrates against the same levels as an unbinned one.</summary>
    public IReadOnlyList<int> SupportedBins => [1, 2, 3, 4];

    public IReadOnlyList<PixelDataFormat> SupportedPixelDataFormats
        => BitDepth > 8 ? [PixelDataFormat.RAW8, PixelDataFormat.RAW16] : [PixelDataFormat.RAW8];

    // ---- What the body can do -------------------------------------------------------------------

    /// <summary>False: external triggering is not exposed. The binding uses the SOFTWARE trigger
    /// internally to take single frames, which is not what this flag describes.</summary>
    public bool IsTriggerCamera => false;

    public bool HasMechanicalShutter => false;

    public bool HasCooler => Model.Has(FLAG_TEC);

    public bool HasST4Port => Model.Has(FLAG_ST4);

    public bool HasThreeChannelWhiteBalance => false;

    // ---- Controls -------------------------------------------------------------------------------

    /// <summary>Zero: the SDK states no electrons-per-ADU figure, so none is invented.</summary>
    public double ElectronPerADU => 0d;

    public bool TryGetControlRange(CMOSControlType ctrlType, out int min, out int max)
    {
        if (Session is { } session)
        {
            return session.TryGetControlRange(ctrlType, out min, out max);
        }

        min = max = 0;
        return false;
    }

    /// <summary>
    /// False: in RAW mode 1 the SDK applies NO white balance at all (mode -1 is the one that applies its
    /// corrections), so there is no gain here to set to neutral, and a mono body has none anyway.
    /// </summary>
    public bool TryGetWhiteBalanceRange(out int min, out int max, out int neutral)
    {
        min = max = neutral = 0;
        return false;
    }

    public CMOSErrorCode GetControlValue(CMOSControlType controlType, out int value, out bool isAuto)
    {
        isAuto = false;
        if (Session is not { } session)
        {
            value = 0;
            return CMOSErrorCode.CameraClosed;
        }

        return session.GetControlValue(controlType, out value);
    }

    /// <summary>Sets a control. Auto modes are refused: the binding switches auto exposure off at open
    /// so that a frame's settings are the ones the caller asked for.</summary>
    public CMOSErrorCode SetControlValue(CMOSControlType controlType, int value, bool isAuto = false)
        => isAuto ? CMOSErrorCode.InvalidControlType
            : Session is { } session ? session.SetControlValue(controlType, value)
            : CMOSErrorCode.CameraClosed;

    // ---- Guiding --------------------------------------------------------------------------------

    /// <summary>True: <c>Toupcam_ST4PlusGuide</c> takes the duration in milliseconds and times the
    /// pulse on the device.</summary>
    public bool CanPulseGuideForDuration => true;

    public CMOSErrorCode PulseGuideOn(GuideDirection direction, TimeSpan duration)
        => Session is { } session
            ? ToDALError(session.PulseGuide((uint)direction, (uint)Math.Clamp(duration.TotalMilliseconds, 1, int.MaxValue)))
            : CMOSErrorCode.CameraClosed;

    /// <summary>
    /// Refused: the device-timed form is what this SDK offers, and an untimed pulse would have to be
    /// started with a duration nobody chose. <see cref="CanPulseGuideForDuration"/> steers callers away
    /// from this.
    /// </summary>
    public CMOSErrorCode PulseGuideOn(GuideDirection direction) => CMOSErrorCode.GeneralError;

    public CMOSErrorCode PulseGuideOff(GuideDirection direction)
        => Session is { } session
            ? ToDALError(session.PulseGuide(ST4_STOP, 0))
            : CMOSErrorCode.CameraClosed;

    // ---- Exposure -------------------------------------------------------------------------------

    /// <summary>Triggers one frame. <see cref="StartDarkExposure"/> is the same call: there is no
    /// mechanical shutter, so a dark is made by capping the telescope, not by the driver.</summary>
    public CMOSErrorCode StartLightExposure()
        => Session is { } session ? ToDALError(session.BeginExposure()) : CMOSErrorCode.CameraClosed;

    /// <inheritdoc cref="StartLightExposure"/>
    public CMOSErrorCode StartDarkExposure() => StartLightExposure();

    public CMOSErrorCode StopExposure()
        => Session is { } session ? ToDALError(session.CancelExposure()) : CMOSErrorCode.CameraClosed;

    public CMOSErrorCode GetExposureStatus(out ExposureStatus exposureStatus)
    {
        if (Session is not { } session)
        {
            exposureStatus = ExposureStatus.Idle;
            return CMOSErrorCode.CameraClosed;
        }

        exposureStatus = session.ExposureStatus;
        return session.IsDisconnected ? CMOSErrorCode.CameraRemoved : CMOSErrorCode.Success;
    }

    // ---- Region of interest ---------------------------------------------------------------------

    public CMOSErrorCode GetStartPosition(out int startX, out int startY)
    {
        (startX, startY) = Session is { } session ? (session.RoiX, session.RoiY) : (0, 0);
        return Session is null ? CMOSErrorCode.CameraClosed : CMOSErrorCode.Success;
    }

    public CMOSErrorCode SetStartPosition(int startX, int startY)
        => Session is { } session ? ToDALError(session.SetStart(startX, startY), CMOSErrorCode.InvalidSize) : CMOSErrorCode.CameraClosed;

    /// <summary>The frame as the SDK WILL deliver it (<c>get_FinalSize</c>), so a caller comparing this
    /// with what it asked for sees any rounding the SDK applied rather than its own request echoed.</summary>
    public CMOSErrorCode GetROIFormat(out int width, out int height, out int bin, out PixelDataFormat pixelDataFormat)
    {
        width = height = bin = 0;
        pixelDataFormat = PixelDataFormat.RAW8;
        if (Session is not { } session)
        {
            return CMOSErrorCode.CameraClosed;
        }

        if (!session.TryGetFinalSize(out width, out height))
        {
            return CMOSErrorCode.GeneralError;
        }

        bin = session.Bin;
        pixelDataFormat = session.Bits16 ? PixelDataFormat.RAW16 : PixelDataFormat.RAW8;
        return CMOSErrorCode.Success;
    }

    public CMOSErrorCode SetROIFormat(int width, int height, int bin, PixelDataFormat pixelDataFormat)
    {
        if (Session is not { } session)
        {
            return CMOSErrorCode.CameraClosed;
        }

        if (pixelDataFormat is not (PixelDataFormat.RAW8 or PixelDataFormat.RAW16)
            || (pixelDataFormat is PixelDataFormat.RAW16 && BitDepth <= 8))
        {
            return CMOSErrorCode.InvalidImageFormat;
        }

        return ToDALError(session.Configure(width, height, bin, pixelDataFormat is PixelDataFormat.RAW16), CMOSErrorCode.InvalidSize);
    }

    // ---- The frame ------------------------------------------------------------------------------

    public CMOSErrorCode GetDataAfterExposure(IntPtr buffer, int bufferSize)
    {
        if (Session is not { } session)
        {
            return CMOSErrorCode.CameraClosed;
        }

        if (!session.TryGetFinalSize(out var width, out var height))
        {
            return CMOSErrorCode.GeneralError;
        }

        return (long)width * height * (session.Bits16 ? 2 : 1) > bufferSize
            ? CMOSErrorCode.BufferTooSmall
            : ToDALError(session.PullFrame(buffer));
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private static int DepthFromFlags(ToupcamModel model)
        => model.Has(FLAG_RAW16) ? 16
            : model.Has(FLAG_RAW14) ? 14
            : model.Has(FLAG_RAW12) ? 12
            : model.Has(FLAG_RAW11) ? 11
            : model.Has(FLAG_RAW10) ? 10
            : 8;

    private static uint FourCc(char a, char b, char c, char d) => a | (uint)b << 8 | (uint)c << 16 | (uint)d << 24;

    /// <summary>
    /// Maps an SDK HRESULT onto the DAL's code. Where no DAL code says the same thing, the answer is
    /// <see cref="CMOSErrorCode.GeneralError"/> rather than a closer-sounding code that asserts a cause
    /// the SDK did not report.
    /// </summary>
    /// <param name="hr">The SDK's result.</param>
    /// <param name="invalidArgument">What <c>E_INVALIDARG</c> means at this call site: a ROI call
    /// rejects a SIZE, a control call a value OUT OF RANGE.</param>
    internal static CMOSErrorCode ToDALError(int hr, CMOSErrorCode invalidArgument = CMOSErrorCode.OutOfBoundary) => hr switch
    {
        >= 0 => CMOSErrorCode.Success,
        E_NOTIMPL => CMOSErrorCode.InvalidControlType,
        E_INVALIDARG => invalidArgument,
        E_PENDING => CMOSErrorCode.ExposureInProgress,
        E_TIMEOUT => CMOSErrorCode.Timeout,
        // "Generally indicates that the conditions are not met", e.g. an option that cannot change
        // while the stream runs: a sequence error, not a failure of the device.
        E_UNEXPECTED => CMOSErrorCode.InvalidSequence,
        _ => CMOSErrorCode.GeneralError,
    };
}
