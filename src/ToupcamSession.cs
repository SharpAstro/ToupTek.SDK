using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using static ToupTek.SDK.ToupcamConstants;

namespace ToupTek.SDK;

/// <summary>
/// One opened camera: its SDK handle, the stream configuration, and the state its event callback
/// reports.
/// </summary>
/// <remarks>
/// <para><b>Why this is a class held in a registry, and not fields of the device struct.</b> The DAL
/// contract is a STRUCT (<see cref="TianWen.DAL.INativeDeviceInfo"/> is implemented by a value type),
/// and <c>DALCameraDriver</c> calls members on copies of it. Player One and ZWO get away with that
/// because every call carries only an integer camera id. A ToupTek camera has live state an id cannot
/// carry: the <c>HToupcam</c> handle, and a frame-ready flag written from the SDK's own callback thread.
/// So the struct carries only the key, and every copy reaches the same session through
/// <see cref="Find"/>.</para>
/// <para><b>Open is reference counted.</b> Discovery opens a camera to read its serial and closes it
/// again; a connected driver keeps it open. Without a count, a discovery pass running while a driver is
/// connected would close the driver's handle out from under it.</para>
/// <para><b>Capture is the SDK's software trigger over pull mode.</b> The camera is put in trigger mode
/// (<c>TOUPCAM_OPTION_TRIGGER = 1</c>) before the stream starts, so it produces nothing until
/// <c>Toupcam_Trigger(h, 1)</c> asks for one frame; the callback marks it ready on
/// <c>TOUPCAM_EVENT_IMAGE</c>; and <c>Toupcam_PullImageV4</c> copies it into the caller's buffer. That is
/// the DAL's start / poll / read shape exactly, with no video stream running between exposures.</para>
/// </remarks>
internal sealed unsafe partial class ToupcamSession
{
    private const int StateIdle = 0;
    private const int StateExposing = 1;
    private const int StateReady = 2;
    private const int StateFailed = 3;

    // Why a lock, against the standing rule: an open and a close are each a NATIVE call that must happen
    // exactly once per transition of the reference count, and a CAS on a dictionary cannot hold a native
    // Toupcam_Open inside it. Both paths are connect / disconnect / discovery, never a render thread, and
    // each takes the lock for one open or close, so it is uncontended in practice.
    private static readonly Lock RegistryLock = new Lock();
    private static readonly Dictionary<string, ToupcamSession> Sessions = new Dictionary<string, ToupcamSession>();

    private readonly GCHandle _self;
    private int _references;
    private int _state = StateIdle;
    private int _disconnected;

    private ToupcamSession(ToupcamDeviceEntry entry, ToupcamApi api, nint handle)
    {
        Entry = entry;
        Api = api;
        Handle = handle;
        _self = GCHandle.Alloc(this);
        MaxBitDepth = api.GetMaxBitDepth(handle) is var depth and > 0 ? depth : 8;
        SerialNumber = ReadFixed(api.GetSerialNumber, handle, 32);
        FirmwareVersion = ReadFixed(api.GetFwVersion, handle, 16);
        RoiWidth = entry.Model.Resolutions.Length > 0 ? entry.Model.Resolutions[0].Width : 0;
        RoiHeight = entry.Model.Resolutions.Length > 0 ? entry.Model.Resolutions[0].Height : 0;
    }

    internal ToupcamDeviceEntry Entry { get; }

    internal ToupcamApi Api { get; }

    internal nint Handle { get; }

    /// <summary>The body's converter depth as the SDK reports it for this handle (12 on an IMX678).</summary>
    internal int MaxBitDepth { get; }

    /// <summary>The factory serial, or null when the body reports none.</summary>
    internal string? SerialNumber { get; }

    /// <summary>
    /// The camera firmware, kept because this family has a history of firmware and SDK versions having
    /// to be matched: SharpCap's forum records ToupTek IMX585 bodies garbling HDR frames until their
    /// firmware was updated, after which older SDKs misbehaved instead. A frame report that names both
    /// can be matched to a pairing.
    /// </summary>
    internal string? FirmwareVersion { get; }

    // ROI, in BINNED pixels as the DAL expresses it. The SDK's put_Roi takes sensor coordinates, so
    // ApplyRoi multiplies back.
    internal int Bin { get; private set; } = 1;
    internal int RoiX { get; private set; }
    internal int RoiY { get; private set; }
    internal int RoiWidth { get; private set; }
    internal int RoiHeight { get; private set; }
    internal bool Bits16 { get; private set; }

    internal bool IsDisconnected => Volatile.Read(ref _disconnected) != 0;

    internal static string KeyOf(ToupcamDeviceEntry entry) => $"{entry.Brand.LibraryName}|{entry.Id}";

    internal static ToupcamSession? Find(string key)
    {
        lock (RegistryLock)
        {
            return Sessions.TryGetValue(key, out var session) ? session : null;
        }
    }

    /// <summary>Opens the camera, or takes another reference to it if it is already open.</summary>
    internal static bool Acquire(ToupcamDeviceEntry entry)
    {
        var key = KeyOf(entry);
        lock (RegistryLock)
        {
            if (Sessions.TryGetValue(key, out var existing))
            {
                existing._references++;
                return true;
            }

            if (entry.Brand.Api is not { } api)
            {
                return false;
            }

            var handle = ToupcamNativeText.WithPlatformString(entry.Id, id => api.Open((void*)id));
            if (handle == 0)
            {
                return false;
            }

            var session = new ToupcamSession(entry, api, handle);
            if (!session.Start())
            {
                session.Shutdown();
                return false;
            }

            session._references = 1;
            Sessions[key] = session;
            return true;
        }
    }

    /// <summary>Drops a reference, closing the camera with the last one.</summary>
    internal static bool Release(string key)
    {
        lock (RegistryLock)
        {
            if (!Sessions.TryGetValue(key, out var session))
            {
                return false;
            }

            if (--session._references > 0)
            {
                return true;
            }

            Sessions.Remove(key);
            session.Shutdown();
            return true;
        }
    }

    /// <summary>
    /// Resets the camera as a replug would (<c>TOUPCAM_OPTION_DEVICE_RESET</c>) and forgets the session,
    /// whatever its reference count: the handle is dead once the device drops off the bus.
    /// </summary>
    /// <remarks>
    /// Measured on the G3M678M: a camera that had stopped delivering frames (a video stream that ran
    /// 163 frames and stopped, every call still succeeding, and stayed stopped across close and
    /// reopen) delivered again about eight seconds after this, with no cable touched. Every setting is
    /// back at the camera's default afterwards, so the caller re-opens and re-applies.
    /// </remarks>
    internal static int Reset(string key)
    {
        lock (RegistryLock)
        {
            if (!Sessions.Remove(key, out var session))
            {
                return E_UNEXPECTED;
            }

            var hr = session.Api.PutOption(session.Handle, OPTION_DEVICE_RESET, 1);
            session.Shutdown();
            return hr;
        }
    }

    /// <summary>
    /// Configures the stream and starts it in trigger mode.
    /// </summary>
    /// <remarks>
    /// <para>ORDER matters, because two of these options are refused once the stream runs
    /// (<c>E_UNEXPECTED</c>, per the SDK manual's option table): RAW and UPSIDE_DOWN.</para>
    /// <para><b>UPSIDE_DOWN defaults to 1 on Windows and 0 elsewhere</b>, the Windows default following
    /// the bottom-up DIB convention. The DAL's frames are top-down, so without this every Windows frame
    /// would arrive vertically flipped and nothing would report it; a model that does not implement the
    /// option (E_NOTIMPL) is taken as already top-down.</para>
    /// <para><b>RAW = 1</b> is the sensor data with no processing at all, which is what an astronomical
    /// frame must be; -1 would apply the SDK's flat, dark, fixed-pattern, black and white balance
    /// corrections to it.</para>
    /// <para><b>ZERO_PADDING = 0</b> is the SDK default, restated so it cannot drift. Despite the
    /// header's "0 => high", it delivers a 12-bit sample LEFT-ALIGNED in the 16-bit word (measured, see
    /// <see cref="ToupcamCamera.DeliversContainerScaledPixels"/>).</para>
    /// <para>Auto exposure is switched off: a frame's exposure and gain are what the caller set.</para>
    /// </remarks>
    private bool Start()
    {
        _ = Api.PutOption(Handle, OPTION_UPSIDE_DOWN, 0);
        if (!Succeeded(Api.PutOption(Handle, OPTION_RAW, 1)))
        {
            return false;
        }

        Bits16 = MaxBitDepth > 8 && Succeeded(Api.PutOption(Handle, OPTION_BITDEPTH, 1));
        _ = Api.PutOption(Handle, OPTION_ZERO_PADDING, 0);
        _ = Api.PutAutoExpoEnable(Handle, 0);
        if (!Succeeded(Api.PutOption(Handle, OPTION_TRIGGER, TRIGGER_SOFTWARE)))
        {
            return false;
        }

        return Succeeded(Api.StartPullModeWithCallback(Handle, &OnEvent, GCHandle.ToIntPtr(_self)));
    }

    /// <summary>Stop before close, and close before freeing the callback context: stopping is what
    /// guarantees no further callback can arrive carrying a pointer to this object.</summary>
    private void Shutdown()
    {
        _ = Api.Stop(Handle);
        Api.Close(Handle);
        _self.Free();
    }

    // ---- Exposure state ---------------------------------------------------------------------------

    internal int BeginExposure()
    {
        DrainPendingFrames();
        Volatile.Write(ref _state, StateExposing);
        var hr = Api.Trigger(Handle, 1);
        if (!Succeeded(hr))
        {
            Volatile.Write(ref _state, StateIdle);
        }

        return hr;
    }

    internal int CancelExposure()
    {
        var hr = Api.Trigger(Handle, 0);
        Volatile.Write(ref _state, StateIdle);
        return hr;
    }

    internal TianWen.DAL.ExposureStatus ExposureStatus => Volatile.Read(ref _state) switch
    {
        StateExposing => TianWen.DAL.ExposureStatus.Working,
        StateReady => TianWen.DAL.ExposureStatus.Success,
        StateFailed => TianWen.DAL.ExposureStatus.Failed,
        _ => TianWen.DAL.ExposureStatus.Idle,
    };

    /// <summary>Copies the finished frame into <paramref name="buffer"/> and returns to idle.</summary>
    internal int PullFrame(nint buffer)
    {
        // pInfo is not optional in every build of the SDK, so it gets somewhere to write.
        // ToupcamFrameInfoV4 is ~104 bytes; the slack is deliberate.
        var info = stackalloc byte[512];
        var hr = Api.PullImageV4(Handle, (void*)buffer, 0, 0, 0, info);
        if (Succeeded(hr))
        {
            Volatile.Write(ref _state, StateIdle);
        }

        return hr;
    }

    /// <summary>The frame size the SDK will deliver, after ROI and binning.</summary>
    internal bool TryGetFinalSize(out int width, out int height)
    {
        int w, h;
        if (Succeeded(Api.GetFinalSize(Handle, &w, &h)))
        {
            (width, height) = (w, h);
            return true;
        }

        (width, height) = (0, 0);
        return false;
    }

    /// <summary>
    /// Discards any frame still queued from an exposure nobody read, so the NEXT read cannot return it.
    /// </summary>
    /// <remarks>
    /// Pull mode queues frames. If a caller abandoned an exposure after it finished (a cancelled capture,
    /// a timeout above this layer), its frame is still waiting, and without this the following trigger's
    /// read would hand back the OLD frame under the new exposure's metadata: a plausible picture with
    /// the wrong exposure time, not an error.
    /// </remarks>
    private void DrainPendingFrames()
    {
        if (Volatile.Read(ref _state) is not StateReady || !TryGetFinalSize(out var w, out var h))
        {
            return;
        }

        var scratch = NativeMemory.Alloc((nuint)(w * h * (Bits16 ? 2 : 1)));
        try
        {
            var info = stackalloc byte[512];
            while (Succeeded(Api.PullImageV4(Handle, scratch, 0, 0, 0, info)))
            {
            }
        }
        finally
        {
            NativeMemory.Free(scratch);
        }
    }

    // ---- Stream configuration -----------------------------------------------------------------------

    /// <summary>Sets bin, bit depth and size together, then re-applies the ROI in sensor coordinates.</summary>
    internal int Configure(int width, int height, int bin, bool bits16)
    {
        if (bin != Bin)
        {
            var hr = Api.PutOption(Handle, OPTION_BINNING, bin == 1 ? 1 : BINNING_AVERAGE | bin);
            if (!Succeeded(hr))
            {
                return hr;
            }

            Bin = bin;
        }

        if (bits16 != Bits16)
        {
            var hr = Api.PutOption(Handle, OPTION_BITDEPTH, bits16 ? 1 : 0);
            if (!Succeeded(hr))
            {
                return hr;
            }

            Bits16 = bits16;
        }

        RoiWidth = width;
        RoiHeight = height;
        return ApplyRoi();
    }

    internal int SetStart(int x, int y)
    {
        RoiX = x;
        RoiY = y;
        return ApplyRoi();
    }

    /// <summary>
    /// <c>Toupcam_put_Roi</c> in SENSOR coordinates: the manual states a coordinate is always relative
    /// to the original resolution, "even that the image has been ... digital binning". Offsets and sizes
    /// must be even and at least 8, so they are rounded down to even here rather than refused.
    /// </summary>
    private int ApplyRoi()
    {
        // The SIZE wins and the start follows it. DALCameraDriver sets the format before the start, so
        // a full-size request arriving after a sub-frame at (100, 50) would otherwise put a 3840-wide
        // window at x = 100 and be refused (E_INVALIDARG): measured on the G3M678M. Clamping the start
        // into the sensor keeps the request valid; the start the caller sets next then lands as asked.
        var (maxW, maxH) = Entry.Model.Resolutions.Length > 0 ? Entry.Model.Resolutions[0] : (int.MaxValue, int.MaxValue);
        RoiX = Math.Clamp(RoiX, 0, Math.Max(0, maxW / Bin - RoiWidth));
        RoiY = Math.Clamp(RoiY, 0, Math.Max(0, maxH / Bin - RoiHeight));
        var x = (uint)(RoiX * Bin) & ~1u;
        var y = (uint)(RoiY * Bin) & ~1u;
        var w = (uint)(RoiWidth * Bin) & ~1u;
        var h = (uint)(RoiHeight * Bin) & ~1u;
        return Api.PutRoi(Handle, x, y, w, h);
    }

    // ---- Events -------------------------------------------------------------------------------------

    /// <summary>
    /// The SDK's event callback. Runs on an SDK thread, so it only flips state: the manual forbids
    /// calling back into Close, Stop, put_Roi or several options from here (they deadlock or fail with
    /// E_WRONG_THREAD).
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnEvent(uint nEvent, nint context)
    {
        if (context != 0 && GCHandle.FromIntPtr(context).Target is ToupcamSession session)
        {
            session.Signal(nEvent);
        }
    }

    private void Signal(uint nEvent)
    {
        switch (nEvent)
        {
            case EVENT_IMAGE:
                _ = Interlocked.CompareExchange(ref _state, StateReady, StateExposing);
                break;
            case EVENT_DISCONNECTED:
                Volatile.Write(ref _disconnected, 1);
                Volatile.Write(ref _state, StateFailed);
                break;
            case EVENT_TRIGGERFAIL or EVENT_ERROR or EVENT_NOFRAMETIMEOUT:
                _ = Interlocked.CompareExchange(ref _state, StateFailed, StateExposing);
                break;
        }
    }

    private static string? ReadFixed(delegate* unmanaged[Stdcall]<nint, byte*, int> read, nint handle, int size)
    {
        var buffer = stackalloc byte[size + 1];
        new Span<byte>(buffer, size + 1).Clear();
        if (!Succeeded(read(handle, buffer)))
        {
            return null;
        }

        var text = ToupcamNativeText.FromAnsiBuffer(buffer, size);
        // A missing serial reads as empty or all zeros; neither is an identity.
        return text.Length == 0 || text.Trim('0').Length == 0 ? null : text;
    }
}
