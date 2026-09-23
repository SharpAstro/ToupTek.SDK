using System.Collections.Generic;
using TianWen.DAL;

namespace ToupTek.SDK;

/// <summary>
/// Enumerates every connected camera across every ToupTek-family library that is installed, shaped
/// like the ZWO, QHY and Player One iterators so a consumer reads one pattern for all of them.
/// </summary>
/// <remarks>
/// The list is taken once, in <see cref="DeviceCount"/>, which
/// <see cref="NativeDeviceIteratorBase{TDeviceInfo}"/> calls before any <see cref="GetDeviceInfo"/>;
/// indexing a second, fresh enumeration could pair an index with a different camera after a replug.
/// </remarks>
public sealed class DeviceIterator : NativeDeviceIteratorBase<ToupcamCamera>
{
    private readonly List<ToupcamCamera> _cameras = new List<ToupcamCamera>();

    protected override int DeviceCount()
    {
        _cameras.Clear();
        foreach (var brand in ToupcamBrand.All)
        {
            foreach (var entry in ToupcamEnumeration.Enumerate(brand))
            {
                _cameras.Add(new ToupcamCamera(entry));
            }
        }

        return _cameras.Count;
    }

    protected override ToupcamCamera? GetDeviceInfo(int index)
        => index >= 0 && index < _cameras.Count ? _cameras[index] : null;
}
