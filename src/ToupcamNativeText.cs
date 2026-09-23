using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ToupTek.SDK;

/// <summary>
/// The SDK's strings, which are <c>wchar_t</c> (UTF-16) on Windows and <c>char</c> everywhere else.
/// </summary>
/// <remarks>
/// <b>Only the enumeration and the camera id follow the platform.</b> The fixed buffers the SDK fills
/// by handle (serial number, firmware version) are <c>char</c> on every platform, which is why they are
/// read through <see cref="FromAnsiBuffer"/> rather than here.
/// </remarks>
internal static unsafe class ToupcamNativeText
{
    /// <summary>Bytes per character in the SDK's platform strings.</summary>
    internal static readonly int CharSize = OperatingSystem.IsWindows() ? 2 : 1;

    internal static string FromPointer(nint text)
        => text == 0
            ? string.Empty
            : (OperatingSystem.IsWindows() ? Marshal.PtrToStringUni(text) : Marshal.PtrToStringUTF8(text)) ?? string.Empty;

    /// <summary>A NUL-terminated platform string in a fixed buffer of <paramref name="maxChars"/>.</summary>
    internal static string FromFixedBuffer(byte* buffer, int maxChars)
    {
        if (OperatingSystem.IsWindows())
        {
            var chars = (char*)buffer;
            var length = 0;
            while (length < maxChars && chars[length] != '\0')
            {
                length++;
            }

            return new string(chars, 0, length);
        }

        return FromAnsiBuffer(buffer, maxChars);
    }

    /// <summary>A NUL-terminated <c>char</c> buffer of at most <paramref name="maxBytes"/>, trimmed.</summary>
    internal static string FromAnsiBuffer(byte* buffer, int maxBytes)
    {
        var length = 0;
        while (length < maxBytes && buffer[length] != 0)
        {
            length++;
        }

        return Encoding.UTF8.GetString(buffer, length).Trim();
    }

    /// <summary>
    /// Calls <paramref name="use"/> with <paramref name="text"/> as a NUL-terminated platform string.
    /// </summary>
    /// <remarks>A pinned .NET string is already NUL-terminated UTF-16, so Windows needs no copy.</remarks>
    internal static T WithPlatformString<T>(string text, Func<nint, T> use)
    {
        if (OperatingSystem.IsWindows())
        {
            fixed (char* chars = text)
            {
                return use((nint)chars);
            }
        }

        var bytes = new byte[Encoding.UTF8.GetByteCount(text) + 1];
        Encoding.UTF8.GetBytes(text, bytes);
        fixed (byte* p = bytes)
        {
            return use((nint)p);
        }
    }
}
