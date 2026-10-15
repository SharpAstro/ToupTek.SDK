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
  as a trigger failure or a generic error, fails the exposure in progress. For a TRIGGERED exposure
  the SDK arms them against when the frame is due, not wall time: three 5 s triggered exposures under
  tighter values (0.5 s and 1 s) raised nothing but the frame, and 3 s idle between triggers raised
  nothing. A video stream is different, below.

### Long exposures under video

**In video the timeouts count from the last packet**, and the camera sends nothing while it
exposes, so with trigger mode's 1 s and 2 s a long exposure is a "stall". Measured 2026-10-15 through
the DAL (`longexp`, below), 640x480 at (1716, 546), 8 bits, read with TianWen's 250 ms wait:

| Run | 1 s / 2 s left armed | Armed from the exposure |
|---|---|---|
| (a) a stream at 3 s, 30 s | 0 frames, 5 faults, each 1.03 to 1.04 s after the start | 10 frames, 3.00 s apart, 0 faults |
| (b) 5 ms, switched to 3 s after 3 s, then 30 s | 1 frame (in flight at 5 ms), 5 faults, each 1.03 to 1.05 s after the last frame | 10 frames at 3 s, 0 faults |
| (c) 3 s, switched to 5 ms after two long frames, then 30 s | (not run) | 0 faults: the frame in flight came at once, cut short, then 5 ms frames at 196.7 fps |

So a stream arms its own: no-packet at the exposure plus 1 s, no-frame at the exposure plus 2 s,
when it starts and when the exposure is set while it runs. A longer exposure is armed at once; a
shorter one only once two more frames have arrived, by the reader (`GetVideoData`), never in the
SDK's callback, since a camera that finishes the frame in flight would otherwise have it called a
stall (this one cuts it short, so the wait costs it about 10 ms). The stop puts trigger mode's 1 s and
2 s back. A real stall during a long exposure is therefore reported about the exposure plus 1 s after
the last frame.

**Stopping a long-exposure stream is slow**: 6.0 to 6.6 s at 3 s. The switch back to trigger mode
appears to wait out the exposure in flight, and the stop's own wait for the camera to go quiet is
twice the exposure plus 50 ms, capped at 3 s. In 55 traced stops at short exposures no frame arrived
after the switch, so the cap could likely come down; that is not measured at long exposures, so it
stays.
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
- **A restart is usually enough, a reset the last resort.** A stop and start of the stream on the same
  handle brought 10 of 11 stalls in the soak back (below), each 6.5 s from the last frame to the first
  again: 1 s to notice, about 5.2 s in the stop, the rest the start. The eleventh did not come back on
  a restart nor on a close and open, and did on `ResetDevice`, 30.4 s in all. Nudging the stalled
  stream instead (the speed level, or the window by 2 px, and back) brought none of three back.
  `ResetDevice` and a fresh open streamed again 9 of the 10 times the probe tried it on its own (1.3
  to 2.5 s to reopen; the tenth, before the double open below, stalled again after 304 frames), but
  the vendor's own binding has also seen a reset camera stream 60 s, stall, and then deliver nothing
  on three fresh opens running.
- **`StopVideoCapture` on a stalled camera blocks about 5 s** inside the SDK (the switch back to
  trigger mode waits on the dead transfer) and still succeeds. A stall that begins in the last second
  of a stream shows only as that slow stop.
- **The first handle opened after a reset reads garbage frame info**: the sequence number and the
  timestamp cycle through five fixed values (0, 16777984, 16777216, 65537, 65792) while the pixels and
  the rate are fine. The session therefore opens twice after a reset, which costs about a second, once.
- **The flush is soft** (`TOUPCAM_OPTION_FLUSH = 2`, the library's queue only), leaving the camera's
  DDR alone, because the hard flush (3, or 1) has nothing to catch: a single exposure straight after
  a stream reads its own frame (below). The hard flush was suspected of the stalls first, having
  stalled 12 of 20 streams of 30 to 60 s one morning, but later periods stalled as often without it.
- **The stalls come in periods, they are the camera's, and nothing tried here prevents them.** A run of
  the vendor's own binding (`reference/toupcam.cs`, TIA1's recipe, no flush at all) and of this binding
  side by side gave the same picture through the day:

  | Time (2026-10-15) | This binding | Vendor's binding |
  |---|---|---|
  | 09:44 to 09:55 (hard flush then) | 12 of 20 streams of 30 to 60 s stalled | |
  | 10:00 to 10:12 | 0 of 5 runs of 30 s, 0 in 20 cycles | 0 of 10 runs of 30 s |
  | 10:19 to 10:32 | 5 of 10 runs of 60 s, 2 in 44 cycles | |
  | 10:36 to 10:47 | | 13 of 15 runs of 60 s, the last three, right after a reset, delivering nothing |
  | 11:17, straight after a reset | 0 of 5 runs of 60 s, 0 in 20 cycles, 0 in 10 pans | |
  | 11:26 | 5 of 5 runs of 60 s | |

  They cluster, with no fixed relation to a reset: after the soak's reset, eight in the first nine
  minutes and none in the next nineteen; after the recover arm's, none for thirteen minutes and three
  in the next seven. Not the machine's load: 11:17 and 11:26 ran at the same 100 percent CPU. Not
  thread priority (`TOUPCAM_OPTION_THREAD_PRIORITY` 3, a High priority class) or queue depth
  (`FRONTEND_DEQUE_LENGTH` 16), which were tried, nor a bit-depth change on the running stream, which
  stalled no more often than none. So a caller of a ToupTek stream has to expect stalls, with no
  warning (below): on any answer but `Success` or `Timeout`, stop and start the stream, then close and
  open, then reset. The probe's stress (some 35 stalls, a dozen resets and hard flushes) may itself
  have pushed this body into its bad periods; that is not ruled out.
- **Frames are lost only where the readout sets the rate**, as a frame with garbage info whose next
  good sequence number skips one: at 2 ms at every speed level, and at 5 ms at levels 1 and 0, whose
  480 rows take 7.6 and 10.2 ms; never at 15 ms, and never at 5 ms at level 2, whose readout (4.98 ms)
  fits inside the exposure (below). Each is counted as one frame lost, which is what the camera's
  count says; none was the ring's (it discarded nothing).

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
  Offsets and sizes must be even, and an odd one is rounded down rather than refused, so
  `GetRoiSteps` declares steps of 2 for all four, and a caller snaps to them.
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
dotnet run --project tools/ToupcamVideoProbe -c Release -- all      # rate, pan and stall
dotnet run --project tools/ToupcamVideoProbe -c Release -- rate --seconds 30
dotnet run --project tools/ToupcamVideoProbe -c Release -- stall --cycles 50
dotnet run --project tools/ToupcamVideoProbe -c Release -- speedstep
dotnet run --project tools/ToupcamVideoProbe -c Release -- soak --rounds 4 --segment 60
dotnet run --project tools/ToupcamVideoProbe -c Release -- recover --rounds 10 --segment 60
dotnet run --project tools/ToupcamVideoProbe -c Release -- longexp --exposure-ms 3000
```

- **rate**: 640x480 at (1716, 546), gain 2252, 1 s settling then 60 s counted, each run on a fresh
  open: three in 8 bits at 5 ms, one in 16 bits, one in 8 bits at 2 ms. Per run: frames, fps by
  `Stopwatch`, `get_FrameRate`, `TryGetDroppedFrames`, stalls. Passes at 190 fps (90 in 16 bits) and
  no stall; the 5 ms runs also at most one frame dropped in 1,800, SharpCap's on this camera (about 55
  in nearly 100,000, the night of 2026-10-07). The 2 ms run's drops are printed, not judged: its bar
  is the rate, about 200, and at 2 ms the readout sets the rate, where this camera loses frames on its
  own (1 in 925 to 1 in 22,809 on 2026-10-15, at every speed level).
- **pan**: ten 64 px `SetStartPosition` moves mid-stream; the shift of the picture is measured by
  matching 16-frame averages (a capped camera's fixed pattern is enough), and the camera's loss per
  pan by its own count.
- **stall**: 20 stop / reconfigure / start cycles on one handle (depth alternating, window stepping
  64 px, 3 s each); a stall must come back as an error within about 2 s and `ResetDevice` then a fresh
  open must stream; one deliberate reset either way; then two 200 ms single exposures at the highest
  gain straight after a stream, which must agree and must not read like the stream.
- **speedstep**: the speed level changed eight times on a running 5 ms stream.
- **soak**: every exposure (5, 15, 2 ms) at every speed level, 8 bits, in 60 s segments round-robin
  from a reset; stalls recovered by the cheapest of restart, reopen and reset, each timed; a table of
  rate, delivered rate (frames over streaming plus recovery time), drops, stalls per hour, and drops in
  the 10 s before each stall.
- **recover**: 5 ms at the top two speed levels, alternating, and a stall nudged (the speed level,
  then the window) before it is stopped.
- **longexp**: exposures longer than the stall timeouts (3 s by default, `--exposure-ms`): a stream
  at it, a 5 ms stream switched to it, and a stream at it switched to 5 ms, each read for 30 s
  (`--seconds`); every long frame printed with its time, every fault with its time.

Exit code 0 when everything passed, 1 when anything failed, 2 with no camera. Measured 2026-10-15 on
the G3M678M, straight after a reset (11:17): **196.8 fps** in 8 bits at 5 ms on three fresh opens
(`get_FrameRate` 197.2 to 197.4), **98.3** in 16 bits, each with 0 dropped and no stall over 60 s;
**200.4** at 2 ms with 13 dropped (1 in 925, reported, not judged; garbage frame info, above); ten pans at
33 ms each, one frame lost apiece; no stall in 20 cycles; the single exposures each reading their own
frame. See "Stalls under video" for the periods when it stalled.

## The speed level, and whether to adapt it

Measured 2026-10-15 with the probe's speedstep, soak (11:43, 36 min) and recover (12:23, 20 min)
arms, 8 bits at the Saturn window, gain 2252. The question was whether a caller should adapt the
bandwidth the way TCP adapts its window (back off on trouble, creep back up when clean), and what a
stall costs.

**Changing the level is cheap.** On a running 5 ms stream, eight changes took 8.5 to 9.3 ms each, the
first frame came straight after, the longest gap was 32 ms, the camera lost 0 or 1 frame, and the
rate a second later was the new level's (200, 133 or 100 fps). No restart, no stall.

**What each cell gave** (soak and recover together; "delivered" counts the time stalls cost):

| Exposure, level | Streamed | fps | Delivered fps | Dropped | Stalls |
|---|---|---|---|---|---|
| 5 ms, 2 | 795 s | 197.1 | 186.7 | 0 in 156,762 | 4 |
| 5 ms, 1 | 824 s | 131.2 | 128.8 | 19 in 108,179 (1 in 5,694) | 1 |
| 5 ms, 0 | 240 s | 98.5 | 98.5 | 6 in 23,653 (1 in 3,942) | 0 |
| 15 ms, 2 | 227 s | 66.4 | 62.7 | 0 | 2 |
| 15 ms, 1 | 240 s | 66.1 | 66.1 | 0 | 0 |
| 15 ms, 0 | 233 s | 66.0 | 64.3 | 0 | 1 |
| 2 ms, 2 | 227 s | 201.1 | 189.9 | 2 in 45,619 | 2 |
| 2 ms, 1 | 240 s | 131.2 | 131.2 | 4 in 31,485 | 0 |
| 2 ms, 0 | 210 s | 98.5 | 85.9 | 2 in 20,644 | 1 (needed a reset) |

The recover arm's two stalls at level 2 took 15.9 s each to undo, because it tried the nudges first;
recovered by a restart alone, as in the soak, they would have cost 6.5 s and the delivered rate at
5 ms, level 2 would read 190.9.

- **At 5 ms the level IS the frame rate**, and level 2 delivers the most frames even counting its
  stalls: 186.7 against 128.8. It loses to level 1 only if stalls come more often than one in 20 s
  (each costing 6.5 s), about ten times the rate measured, and more than even the worst minute
  (two stalls). Backing off at 5 ms costs a third of the frames to save about 3 percent.
- **Where the exposure sets the rate (15 ms), the level costs nothing**: 66 fps at all three. Level 1
  stalled least overall (1 in 1,304 s, against level 2's 8 in 1,249 s and level 0's 2 in 683 s), but
  one stall against eight is thin, and level 0 shows a lower level is not steadier as such.
- **Frames are dropped only where the readout sets the rate**, and all of it is within SharpCap's bar
  except at 2 ms, the camera's ceiling.
- **Nothing warns of a stall**: none of 11 stalls had a frame dropped in the 10 s before it, so a
  back-off driven by losses would never fire in time.
- **Recovery**: a restart on the same handle, 10 of 11, 6.5 s each; a reset, 1 of 11, 30.4 s; a nudge
  of the speed level or the window, 0 of 3, and about 9 s added.

So on this camera an adaptive bandwidth does not pay at 5 ms, and has no early signal to act on. What
does pay: recovering a stall automatically and quickly (restart, then reopen, then reset), and, where
the exposure is longer than the readout at a lower level, taking the lowest such level, which costs
no frames and may stall less.

## Vendor drops

`tools/fetch-natives.ps1` unpacks the SDK zip into `lib/<rid>/`, `include/` and `reference/`, and
writes `lib/SOURCE.txt`. Re-run it against a new zip and commit what changed.

## Licence

The ToupTek SDK is closed source and proprietary to ToupTek; its binaries, header and manual are
redistributed here as other open-source astronomy software does (INDIGO vendors the same files under
`indigo_drivers/ccd_touptek/bin_externals/libtoupcam`). The wrapper code is SharpAstro's.
