# ToupTek.SDK

ToupTek cameras from .NET, and the rebadged families built on the same SDK, implementing SharpAstro's
`TianWen.DAL` device abstraction so a ToupTek body is driven by the same code that drives a ZWO, QHY
or Player One one.

Vendor SDK: **60.32549.20260908** (`lib/SOURCE.txt` names the zip and its sha256). The package does
not track that number, because it is a build counter and a date rather than a version, and eleven
libraries share it.

## One binding, eleven libraries

ToupTek's SDK is rebadged by several vendors under their own library name and function prefix. Every
entry point is resolved at run time against the library's own handle (`NativeLibrary.GetExport` into
unmanaged function pointers, AOT-clean), so one binding covers all of them:

| Brand | Library | Prefix |
|---|---|---|
| ToupTek | `toupcam` | `Toupcam_` |
| Altair | `altaircam` | `Altaircam_` |
| Bresser | `bressercam` | `Bressercam_` |
| MallinCam | `mallincam` | `Mallincam_` |
| Nn | `nncam` | `Nncam_` |
| OGMAVision | `ogmacam` | `Ogmacam_` |
| Omegon | `omegonprocam` | `Omegonprocam_` |
| Orion | `starshootg` | `Starshootg_` |
| Teleskop Service | `tscam` | `Tscam_` |
| SVBONY (ToupTek-based line) | `svbonycam` | `Svbonycam_` |
| Meade | `meadecam` | **`Toupcam_`** |

The table is INDI's (`indi-3rdparty/indi-toupbase/libtoupbase.h`), not recalled. Meade exporting
`Toupcam_` symbols is why resolution is per handle rather than by global name. **Only ToupTek ships
with this package and only ToupTek is verified**; the others work wherever their own library is
installed beside the application, and are unverified until one is plugged in.

## Verified on hardware

A **ToupTek G3M678M** (IMX678 mono, 2.0 um, 3840 x 2160, USB `0547:14BC`, firmware 4.0.2.202300708)
on Windows x64, 2026-09-23. Found through `Toupcam_EnumV2` under Microsoft's in-box WinUSB driver, no
vendor driver install.

- **Pixels are 12-bit LEFT-ALIGNED in 16**, whatever the header's comment on
  `TOUPCAM_OPTION_ZERO_PADDING` suggests: 8,293,901 of 8,294,400 values a multiple of 16, clipping at
  exactly 65520 = 4095 x 16. The SDK reports 16 bits (`get_MaxBitDepth`, `get_RawFormat`,
  `TOUPCAM_FLAG_RAW16`) and 16 is what `BitDepth` declares, so the declared saturation (65535) is
  right; `ToupcamCamera.DeliversContainerScaledPixels` records the measurement.
- **Average binning** (`TOUPCAM_OPTION_BINNING = 0x80 | n`) keeps the ADU scale; at bin 2 the step is
  4, as on Player One.
- **Black level** runs 0 to 7936 in this mode, confirmed by the SDK refusing 7937.
- **Serial** `TP250818143548A337C2B6718F4135B` needs an open handle, and is the identity: the SDK's
  own id is a USB device path, which changes with the port.
- No sensor temperature on this body; the SDK answers `E_NOTIMPL` and the binding reports that.

## Frame rate, and recovering a stalled camera

Measured in video mode (`TOUPCAM_OPTION_TRIGGER = 0`, RAW, speed 2, the default), counting delivered
frames over 3 s, no frames dropped:

| Rows (width 3840) | 8-bit fps | 16-bit fps |
|---|---|---|
| 2160 | 47 | 23 |
| 1080 | 93 | 45 |
| 256 | 362 | 177 |
| 128 | 658 | 323 |
| **40** | **1510** | **740** |

**Height is what counts, width is not**: at 40 rows the rate is identical at 3840, 1920 and 960 wide,
so the ceiling is the sensor's row readout, not USB. 8-bit output runs the sensor about twice as fast
as 16-bit. Below 40 rows it is exposure-bound (1 ms caps at about 925 fps; 500 us and shorter reach
1510).

**The camera can stall, and a configuration change is what sets it off.** The transfer from the
camera stops: every call still succeeds, no frame arrives, and with the no-packet timeout armed the SDK
raises `TOUPCAM_EVENT_NOPACKETTIMEOUT` every ~0.5 s. Measured 2026-09-23:

| Path | Runs | Stalls |
|---|---|---|
| Video, fresh open per run, resolution, bit depth and speed rotating | 80 | 2: once after 895 frames of a stream, once at the start |
| Video, a sweep of the same changes | 48 | 3, all 8-bit |
| Software trigger (the DAL's path), ROI changed every 5 frames | 400 at 8-bit, 400 at 16-bit | 0 |
| Software trigger, bit depth flipped by a close and reopen every 20 frames | 600 | 1 frame never arrived, with NO event, and the next trigger worked |

The stall at the start of a video run survived both a restart of the stream on the same handle and a
close and reopen. `TOUPCAM_OPTION_DEVICE_RESET` ("simulate a replug") cleared it: the camera
re-enumerated under the same serial and streamed again after 2.8 s (0.6 s on another occasion).

What the binding does about it:

- **Both timeouts are armed on every stream** (no-packet 1 s, no-frame 2 s), and either one, as well
  as a trigger failure or a generic error, fails the exposure in progress. The SDK arms them against
  when a frame is due, not wall time: three 5 s triggered exposures under tighter values (0.5 s and
  1 s) raised nothing but the frame, and 3 s idle between triggers raised nothing.
- **The reset is exposed as the DAL's `ResetDevice()`** (`CanResetDevice` is true), and never fired
  from here. TianWen's DAL camera driver gives up a frame the camera never delivers at a deadline, and
  resets the camera before the next exposure after two losses in a row, then finds it again and
  restores its gain, offset, white balance and cooler. A lost trigger with no event can only be caught
  by that deadline, which is why it lives in the driver rather than here.

### Stalls under video

Measured 2026-10-15 through the DAL's video members (`tools/ToupcamVideoProbe`, below), 640x480 at
(1716, 546):

- **A stall is an error, not a timeout.** Once the SDK raises `NOPACKETTIMEOUT`, `NOFRAMETIMEOUT` or
  `ERROR` during a stream, `GetVideoData` answers **`GeneralError`** after handing out the frames that
  came before it (`CameraRemoved` for a disconnect). No DAL code says "the camera is attached but no
  longer sending", `Timeout` would have the caller ask for ever, and `CameraRemoved` would claim a
  cause the SDK did not report. Every stall seen (over 20) surfaced 1.03 to 1.06 s after the last
  frame, the 1 s no-packet timeout, and none went silent.
- **A reset brings it back, usually.** `ResetDevice` and a fresh open streamed again 9 of the 10
  times the probe tried it (1.3 to 2.5 s to reopen; the tenth, before the double open below, stalled
  again after 304 frames), but the vendor's own binding has also seen a reset camera stream 60 s,
  stall, and then deliver nothing on three fresh opens running.
- **`StopVideoCapture` on a stalled camera blocks about 5 s** inside the SDK (the switch back to
  trigger mode waits on the dead transfer) and still succeeds. A stall that begins in the last second
  of a stream shows only as that slow stop.
- **The first handle opened after a reset reads garbage frame info**: the sequence number and the
  timestamp cycle through five fixed values (0, 16777984, 16777216, 65537, 65792) while the pixels and
  the rate are fine. The session therefore opens twice after a reset, which costs about a second, once.
- **A hard flush made stalls far more frequent.** `TOUPCAM_OPTION_FLUSH = 3` (or 1) empties the camera's
  DDR; done beside each switch into and out of video it stalled 12 of 20 streams of 30 to 60 s. The
  binding flushes soft (2, the library's queue only), and a single exposure straight after a stream
  still reads its own frame (below).
- **The rate of stalls changes over a day, and is the camera's, not this binding's.** No thread
  priority (`TOUPCAM_OPTION_THREAD_PRIORITY` 3, a High priority class) or queue depth
  (`FRONTEND_DEQUE_LENGTH` 16) removed them. Around 10:00 the vendor's own binding (`reference/toupcam.cs`,
  TIA1's recipe) streamed 10 of 10 runs of 30 s clean and this binding 5 of 5, with no stall in 20
  stop / reconfigure / start cycles; from 10:30, with the machine at 100 percent CPU from other work,
  this binding stalled 4 of 5 runs of 60 s and the vendor's binding 9 of 10, with or without a
  bit-depth change, and after a reset too. So a caller of a ToupTek stream has to expect stalls: on
  any answer but `Success` or `Timeout`, reset the camera and start again.

## How capture works

Software trigger over pull mode: the camera starts in `TOUPCAM_OPTION_TRIGGER = 1`, so it produces
nothing until `Toupcam_Trigger(h, 1)`; an `[UnmanagedCallersOnly]` event callback marks the frame
ready on `TOUPCAM_EVENT_IMAGE`; `Toupcam_PullImageV4` copies it into the caller's buffer. That is the
DAL's start / poll / read shape with no video stream running between exposures.

**Video is the same SDK stream, free-running.** `StartVideoCapture` switches the running stream to
`TOUPCAM_OPTION_TRIGGER = 0` with the window, format and controls set now, and `StopVideoCapture`
switches it back to 1. The SDK is never stopped, since a stop and restart on one handle set off the
vendor video loop's stalls (14 of 46 runs); the switch itself blocks about 200 ms each way, the SDK
evidently restarting the transfer, so a start costs about 200 ms and a stop 250 to 350.

- **Frames are pulled in the callback**, into a ring of recycled native buffers (64 MiB budget, 4 to
  64 slots: 64 for a 640x480 8-bit window, 320 ms at 200 fps), allocated at the start and kept for
  the next stream. `GetVideoData` copies the oldest into the caller's buffer, in order, waiting on an
  event up to `waitMs`; nothing is allocated per frame. At 200 fps a frame is due every 5 ms, and the
  SDK's own queues are four raw frames and three processed, so the reader's pauses land on the ring,
  not on the camera. A frame that finds the ring full is pulled to scratch and counted dropped.
- **`TryGetDroppedFrames`** is the camera's sequence gaps (`ToupcamFrameInfoV4.seq`) plus the ring's
  discards, since the stream started. The sequence is believed only while it counts by one: a jump
  is counted once the next frame confirms it, a frame with a garbage number (now and then mid-stream
  on a good handle) stands for one frame, and a stream whose sequence never counts answers false
  rather than zero.
- **The stop guards the next single exposure**: it waits for the camera to go quiet (twice the
  exposure plus 50 ms), soft-flushes, and has the next exposure drain. In 55 traced stops no frame
  arrived after the switch, and two 200 ms exposures at the highest gain straight after a stream
  agreed with each other and not with the stream (brightest 0.1 percent at 98 and 100 ADU against 16).
- **A stream refuses what it cannot do mid-stream**: `StartLightExposure`, `SetROIFormat` and
  `GetDataAfterExposure` answer `VideoModeActive`, and `StartVideoCapture` during an exposure
  `ExposureInProgress`.
- **`CanPanRoiWhileStreaming` is true.** `put_Roi` moves the window of a running stream (the G3M678M
  is a `TOUPCAM_FLAG_ROI_HARDWARE` body): in 30 pans of 64 px at 197 fps the picture moved by exactly
  64 px every time, the camera's frame count went on unbroken with exactly one frame lost per pan,
  and nothing stalled. The call blocks its caller 32 to 285 ms while the stream runs on into the ring.
- **`BandwidthOverload` is the speed level** (`put_Speed`, 0 to `get_MaxSpeed`, 2 here and the
  camera's own default), NOT a percentage: the SDK manual equates the levels with the camera's share
  of the link, which is what the DAL's control means, and TianWen's video loop takes the maximum for
  a stream and gives back what it found. ZWO's 40 to 100 is refused rather than mapped, so the 50
  TianWen writes at connect leaves the camera at its fastest level. Setting it is instant (1 ms), no
  restart. **`HighSpeedMode` is not offered**: on ZWO it trades converter depth for speed, which this
  SDK has no way to do, and mapping it onto the same `put_Speed` would make two DAL controls one knob,
  TianWen's `HighSpeedMode = 0` at connect then dropping every single exposure to the slowest level.

Things that are easy to get wrong, each handled once in `ToupcamSession`:

- **`TOUPCAM_OPTION_UPSIDE_DOWN` defaults to 1 on Windows** (the bottom-up DIB convention) and 0
  elsewhere. It is set to 0 before the stream starts, since it cannot change while running, or every
  Windows frame would arrive flipped.
- **`TOUPCAM_OPTION_RAW = 1`**, not -1: -1 applies the SDK's own flat, dark and white balance
  corrections. RAW mode applies no white balance, so none is reported.
- **`put_Roi` takes SENSOR coordinates** even under digital binning; the DAL's are binned, so the
  session multiplies back. The size wins over a stale start, since the DAL sets size before start.
- **The DAL's device type is a struct**, copied freely, while a ToupTek camera has live state (the
  handle, the ready flag). The struct carries only a key into a reference-counted session registry.

**Conversion gain (`TOUPCAM_OPTION_CG`: LCG, HCG, HDR) is read, never set.** The G3M678M starts in
HCG and also has an HDR mode (`TOUPCAM_FLAG_CGHDR`). SharpCap keeps this behind an opt-in "Read
Mode" setting, and HDR on this family has depended on matching camera firmware and SDK versions
(SharpCap forum, t=8307), so the camera's default is left alone until the DAL has a read-mode control.

## The video probe

`tools/ToupcamVideoProbe` drives the video path through the DAL members only (plus the binding's
`SdkFrameRate`, which is `get_FrameRate`), the way TianWen's video loop does: `GetVideoData` with a
250 ms wait, `Timeout` asked again, anything else the end of the stream, `BandwidthOverload` at its
top. It needs the camera, holds it for minutes and resets it, so CI does not run it. With nothing
else holding the camera:

```bash
dotnet run --project tools/ToupcamVideoProbe -c Release -- all      # or rate | stall | pan
dotnet run --project tools/ToupcamVideoProbe -c Release -- rate --seconds 30
dotnet run --project tools/ToupcamVideoProbe -c Release -- stall --cycles 50
```

- **rate**: 640x480 at (1716, 546), gain 2252, 1 s settling then 60 s counted, each run on a fresh
  open: three in 8 bits at 5 ms, one in 16 bits, one in 8 bits at 2 ms. Per run: frames, fps by
  `Stopwatch`, `get_FrameRate`, `TryGetDroppedFrames`, stalls. Passes at 190 fps (90 in 16 bits), 0
  dropped, no stall.
- **pan**: ten 64 px `SetStartPosition` moves mid-stream; the shift of the picture is measured by
  matching 16-frame averages (a capped camera's fixed pattern is enough), and the camera's loss per
  pan by its own count.
- **stall**: 20 stop / reconfigure / start cycles on one handle (depth alternating, window stepping
  64 px, 3 s each); a stall must come back as an error within about 2 s and `ResetDevice` then a fresh
  open must stream; one deliberate reset either way; then two 200 ms single exposures at the highest
  gain straight after a stream, which must agree and must not read like the stream.

Exit code 0 when everything passed, 1 when anything failed, 2 with no camera. Measured 2026-10-15 on
the G3M678M, when it did not stall: **196.8 fps** in 8 bits at 5 ms (`get_FrameRate` 197.3), **98.3**
in 16 bits, **200.6** at 2 ms, each with 0 dropped over 60 s; see "Stalls under video" for when it
did.

## Vendor drops

`tools/fetch-natives.ps1` unpacks the SDK zip into `lib/<rid>/`, `include/` and `reference/`, and
writes `lib/SOURCE.txt`. Re-run it against a new zip and commit what changed.

## Licence

The ToupTek SDK is closed source and proprietary to ToupTek; its binaries, header and manual are
redistributed here as other open-source astronomy software does (INDIGO vendors the same files under
`indigo_drivers/ccd_touptek/bin_externals/libtoupcam`). The wrapper code is SharpAstro's.
