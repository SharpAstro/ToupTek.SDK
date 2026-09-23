using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace ToupTek.SDK;

/// <summary>
/// A camera model as the SDK describes it (<c>ToupcamModelV2</c>), copied out of native memory.
/// </summary>
/// <param name="Name">Model name, e.g. <c>G3M678M</c>.</param>
/// <param name="Flag">The <c>TOUPCAM_FLAG_xxx</c> capability bits.</param>
/// <param name="MaxSpeed">Highest speed level; the range is [0, MaxSpeed].</param>
/// <param name="MaxFanSpeed">Highest fan speed; the range is [0, MaxFanSpeed].</param>
/// <param name="XPixelSize">Photosite width in micrometres.</param>
/// <param name="YPixelSize">Photosite height in micrometres.</param>
/// <param name="Resolutions">The preview resolutions, largest first as the SDK lists them.</param>
public sealed record ToupcamModel(
    string Name,
    ulong Flag,
    uint MaxSpeed,
    uint MaxFanSpeed,
    float XPixelSize,
    float YPixelSize,
    ImmutableArray<(int Width, int Height)> Resolutions)
{
    public bool Has(ulong flag) => (Flag & flag) != 0;
}

/// <summary>One enumerated camera: which library found it, and the SDK's opaque id for opening it.</summary>
/// <param name="Brand">The library that enumerated it.</param>
/// <param name="Index">Its position in that library's enumeration, which is NOT stable across a replug.</param>
/// <param name="DisplayName">The SDK's display name: the model name, or a user-defined one.</param>
/// <param name="Id">The opaque id <c>Toupcam_Open</c> takes.</param>
/// <param name="Model">What the model is.</param>
public sealed record ToupcamDeviceEntry(ToupcamBrand Brand, int Index, string DisplayName, string Id, ToupcamModel Model);

/// <summary>
/// <c>Toupcam_EnumV2</c>, read by offset because its layout differs between platforms.
/// </summary>
/// <remarks>
/// <para><b>Neither struct can be one C# declaration.</b> <c>ToupcamDeviceV2</c> is two
/// <c>wchar_t[64]</c> on Windows (256 bytes before the model pointer) and two <c>char[64]</c> elsewhere
/// (128), and <c>ToupcamModelV2</c> starts with a <c>wchar_t*</c> / <c>char*</c> name. So both are read
/// at computed offsets from the platform's character width and pointer size, the way the vendor's own
/// binding branches on <c>IsWindows()</c> for the same fields.</para>
/// <para><b>The 64-bit <c>flag</c> is the one alignment trap.</b> It follows the name pointer, so on a
/// 64-bit target it sits at 8. On 32-bit Windows it is also at 8, because MSVC aligns a
/// <c>long long</c> member to 8; on 32-bit Linux (i386 System V) it is at 4, because that ABI aligns
/// it to 4. Everything after it is 4-byte fields and packs the same way on every target.</para>
/// </remarks>
public static unsafe class ToupcamEnumeration
{
    private const int NameChars = 64;

    public static IReadOnlyList<ToupcamDeviceEntry> Enumerate(ToupcamBrand brand)
    {
        if (brand.Api is not { } api)
        {
            return [];
        }

        var nameBytes = NameChars * ToupcamNativeText.CharSize;
        var entrySize = 2 * nameBytes + IntPtr.Size;
        var buffer = (byte*)NativeMemory.AllocZeroed((nuint)(ToupcamConstants.MaxDevices * entrySize));
        try
        {
            var count = (int)Math.Min(api.EnumV2(buffer), (uint)ToupcamConstants.MaxDevices);
            var entries = new List<ToupcamDeviceEntry>(count);
            for (var i = 0; i < count; i++)
            {
                var entry = buffer + i * entrySize;
                var displayName = ToupcamNativeText.FromFixedBuffer(entry, NameChars);
                var id = ToupcamNativeText.FromFixedBuffer(entry + nameBytes, NameChars);
                var model = *(nint*)(entry + 2 * nameBytes);
                if (id.Length > 0 && model != 0)
                {
                    entries.Add(new ToupcamDeviceEntry(brand, i, displayName, id, ReadModel((byte*)model)));
                }
            }

            return entries;
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    private static ToupcamModel ReadModel(byte* model)
    {
        var flagOffset = IntPtr.Size == 8 || OperatingSystem.IsWindows() ? 8 : 4;
        var name = ToupcamNativeText.FromPointer(*(nint*)model);
        var flag = *(ulong*)(model + flagOffset);

        // After the flag: maxspeed, preview, still, maxfanspeed, ioctrol (unsigned), then xpixsz, ypixsz
        // (float), then ToupcamResolution res[16] of two unsigned each.
        var fields = model + flagOffset + sizeof(ulong);
        var maxSpeed = *(uint*)fields;
        var preview = *(uint*)(fields + 4);
        var maxFanSpeed = *(uint*)(fields + 12);
        var xPixel = *(float*)(fields + 20);
        var yPixel = *(float*)(fields + 24);
        var resolutions = (uint*)(fields + 28);

        var count = (int)Math.Min(preview, 16u);
        var builder = ImmutableArray.CreateBuilder<(int, int)>(count);
        for (var i = 0; i < count; i++)
        {
            builder.Add(((int)resolutions[2 * i], (int)resolutions[2 * i + 1]));
        }

        return new ToupcamModel(name, flag, maxSpeed, maxFanSpeed, xPixel, yPixel, builder.MoveToImmutable());
    }
}
