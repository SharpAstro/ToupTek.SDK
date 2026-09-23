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

## How capture works

Software trigger over pull mode: the camera starts in `TOUPCAM_OPTION_TRIGGER = 1`, so it produces
nothing until `Toupcam_Trigger(h, 1)`; an `[UnmanagedCallersOnly]` event callback marks the frame
ready on `TOUPCAM_EVENT_IMAGE`; `Toupcam_PullImageV4` copies it into the caller's buffer. That is the
DAL's start / poll / read shape with no video stream running between exposures.

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

## Vendor drops

`tools/fetch-natives.ps1` unpacks the SDK zip into `lib/<rid>/`, `include/` and `reference/`, and
writes `lib/SOURCE.txt`. Re-run it against a new zip and commit what changed.

## Licence

The ToupTek SDK is closed source and proprietary to ToupTek; its binaries, header and manual are
redistributed here as other open-source astronomy software does (INDIGO vendors the same files under
`indigo_drivers/ccd_touptek/bin_externals/libtoupcam`). The wrapper code is SharpAstro's.
