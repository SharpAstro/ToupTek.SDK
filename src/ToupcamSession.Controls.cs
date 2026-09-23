using System;
using TianWen.DAL;
using static ToupTek.SDK.ToupcamConstants;

namespace ToupTek.SDK;

/// <summary>The DAL's controls over one open camera. See <see cref="ToupcamCamera"/> for the units.</summary>
internal sealed unsafe partial class ToupcamSession
{
    /// <summary>The raw mosaic as a FOURCC (<c>MAKEFOURCC('R','G','G','B')</c> and so on), or 0.</summary>
    internal uint RawFourCc()
    {
        uint fourCc, bits;
        return Succeeded(Api.GetRawFormat(Handle, &fourCc, &bits)) ? fourCc : 0;
    }

    /// <summary>The bits per pixel <c>Toupcam_get_RawFormat</c> reports for the current mode, or 0.</summary>
    internal int RawBitsPerPixel()
    {
        uint fourCc, bits;
        return Succeeded(Api.GetRawFormat(Handle, &fourCc, &bits)) ? (int)bits : 0;
    }

    /// <summary><c>TOUPCAM_OPTION_CG</c> as it stands (0 LCG, 1 HCG, 2 HDR), or null where the body has
    /// no conversion gain modes.</summary>
    internal int? ConversionGain()
        => Has(FLAG_CG) || Has(FLAG_CGHDR) ? GetOption(OPTION_CG, out var cg) is var hr && Succeeded(hr) ? cg : null : null;

    /// <summary><c>Toupcam_ST4PlusGuide</c>: directions 0 to 3 are North, South, East, West, the same
    /// order as the DAL's <see cref="GuideDirection"/>, and <see cref="ST4_STOP"/> ends a pulse.</summary>
    internal int PulseGuide(uint direction, uint milliseconds) => Api.ST4PlusGuide(Handle, direction, milliseconds);

    private bool Has(ulong flag) => Entry.Model.Has(flag);

    /// <summary>The bit depth the stream runs at NOW, which is what the black level range follows.</summary>
    private int CurrentBitDepth => Bits16 ? MaxBitDepth : 8;

    internal bool TryGetControlRange(CMOSControlType control, out int min, out int max)
    {
        min = max = 0;
        switch (control)
        {
            case CMOSControlType.Exposure:
            {
                uint lo, hi, def;
                if (!Succeeded(Api.GetExpTimeRange(Handle, &lo, &hi, &def)))
                {
                    return false;
                }

                (min, max) = ((int)Math.Min(lo, int.MaxValue), (int)Math.Min(hi, int.MaxValue));
                return true;
            }
            case CMOSControlType.Gain:
            {
                ushort lo, hi, def;
                if (!Succeeded(Api.GetExpoAGainRange(Handle, &lo, &hi, &def)))
                {
                    return false;
                }

                (min, max) = (lo, hi);
                return true;
            }
            case CMOSControlType.Brightness when Has(FLAG_BLACKLEVEL):
                // TOUPCAM_BLACKLEVELn_MAX is 31 x 2^(n - 8): the offset is a count in the CURRENT
                // output's units, so it scales with the bit depth the stream runs at.
                (min, max) = (0, 31 << (CurrentBitDepth - 8));
                return true;
            case CMOSControlType.TargetTemperature when Has(FLAG_TEC_ONOFF):
            {
                int packed;
                if (!Succeeded(Api.GetOption(Handle, OPTION_TECTARGET_RANGE, &packed)))
                {
                    return false;
                }

                // Low 16 bits the minimum, high 16 the maximum, each in tenths of a degree.
                (min, max) = ((short)(packed & 0xffff) / 10, (short)((packed >> 16) & 0xffff) / 10);
                return true;
            }
            case CMOSControlType.CoolerOn when Has(FLAG_TEC_ONOFF):
                (min, max) = (0, 1);
                return true;
            case CMOSControlType.CoolerPowerPercent when Has(FLAG_TEC):
                (min, max) = (0, 100);
                return true;
            case CMOSControlType.FanOn when Has(FLAG_FAN):
                (min, max) = (0, (int)Entry.Model.MaxFanSpeed);
                return true;
            case CMOSControlType.AntiDewHeater when Has(FLAG_HEAT):
            {
                int heatMax;
                if (!Succeeded(Api.GetOption(Handle, OPTION_HEAT_MAX, &heatMax)))
                {
                    return false;
                }

                (min, max) = (0, heatMax);
                return true;
            }
            default:
                return false;
        }
    }

    internal CMOSErrorCode GetControlValue(CMOSControlType control, out int value)
    {
        value = 0;
        int hr;
        switch (control)
        {
            case CMOSControlType.Exposure:
            {
                uint us;
                hr = Api.GetExpoTime(Handle, &us);
                value = (int)Math.Min(us, int.MaxValue);
                break;
            }
            case CMOSControlType.Gain:
            {
                ushort percent;
                hr = Api.GetExpoAGain(Handle, &percent);
                value = percent;
                break;
            }
            case CMOSControlType.Brightness when Has(FLAG_BLACKLEVEL):
                hr = GetOption(OPTION_BLACKLEVEL, out value);
                break;
            case CMOSControlType.TemperatureDeci:
            {
                // Already tenths of a degree, the DAL's own convention for this control.
                short deci;
                hr = Api.GetTemperature(Handle, &deci);
                value = deci;
                break;
            }
            case CMOSControlType.TargetTemperature when Has(FLAG_TEC_ONOFF):
                hr = GetOption(OPTION_TECTARGET, out var targetDeci);
                value = (int)Math.Round(targetDeci / 10d);
                break;
            case CMOSControlType.CoolerOn when Has(FLAG_TEC_ONOFF):
                hr = GetOption(OPTION_TEC, out value);
                break;
            case CMOSControlType.CoolerPowerPercent when Has(FLAG_TEC):
                // The SDK reports the TEC's drive as a VOLTAGE against its configured maximum, both in
                // tenths of a volt, and no percentage at all; the ratio is the nearest honest reading.
                hr = GetOption(OPTION_TEC_VOLTAGE, out var volts);
                if (Succeeded(hr))
                {
                    hr = GetOption(OPTION_TEC_VOLTAGE_MAX, out var voltsMax);
                    value = voltsMax > 0 ? (int)Math.Round(100d * volts / voltsMax) : 0;
                }
                break;
            case CMOSControlType.FanOn when Has(FLAG_FAN):
                hr = GetOption(OPTION_FAN, out value);
                break;
            case CMOSControlType.AntiDewHeater when Has(FLAG_HEAT):
                hr = GetOption(OPTION_HEAT, out value);
                break;
            default:
                return CMOSErrorCode.InvalidControlType;
        }

        return ToupcamCamera.ToDALError(hr);
    }

    internal CMOSErrorCode SetControlValue(CMOSControlType control, int value)
    {
        var hr = control switch
        {
            CMOSControlType.Exposure when value >= 0 => Api.PutExpoTime(Handle, (uint)value),
            CMOSControlType.Gain when value is >= 0 and <= ushort.MaxValue => Api.PutExpoAGain(Handle, (ushort)value),
            CMOSControlType.Brightness when Has(FLAG_BLACKLEVEL) => Api.PutOption(Handle, OPTION_BLACKLEVEL, value),
            CMOSControlType.TargetTemperature when Has(FLAG_TEC_ONOFF) => Api.PutOption(Handle, OPTION_TECTARGET, value * 10),
            CMOSControlType.CoolerOn when Has(FLAG_TEC_ONOFF) => Api.PutOption(Handle, OPTION_TEC, value != 0 ? 1 : 0),
            CMOSControlType.FanOn when Has(FLAG_FAN) => Api.PutOption(Handle, OPTION_FAN, value),
            CMOSControlType.AntiDewHeater when Has(FLAG_HEAT) => Api.PutOption(Handle, OPTION_HEAT, value),
            CMOSControlType.Exposure or CMOSControlType.Gain => E_INVALIDARG,
            _ => E_NOTIMPL,
        };

        return ToupcamCamera.ToDALError(hr);
    }

    private int GetOption(uint option, out int value)
    {
        int v;
        var hr = Api.GetOption(Handle, option, &v);
        value = Succeeded(hr) ? v : 0;
        return hr;
    }
}
