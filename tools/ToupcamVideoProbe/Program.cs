using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using TianWen.DAL;
using ToupTek.SDK;

namespace ToupcamVideoProbe;

/// <summary>
/// Drives a ToupTek camera's video through the DAL members, as TianWen's video loop does, and says
/// whether it holds the rate, loses nothing, surfaces a stall as an error, and leaves single exposures
/// working. See the csproj for how to run it.
/// </summary>
internal static class Program
{
    // The Saturn region of the 2026-10-07 night (tianwen docs/plans/live-planetary-capture.md).
    private const int RoiX = 1716;
    private const int RoiY = 546;
    private const int RoiWidth = 640;
    private const int RoiHeight = 480;
    private const int Gain = 2252;

    /// <summary>What TianWen's video loop asks <c>GetVideoData</c> to wait (DALCameraDriver.VideoFrameWait).</summary>
    private const int FrameWaitMs = 250;

    /// <summary>A stream that delivers nothing for this long while the binding still answers Timeout
    /// has stalled WITHOUT saying so, which is the failure this probe exists to rule out.</summary>
    private static readonly TimeSpan SilentStallAfter = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan ReappearTimeout = TimeSpan.FromSeconds(15);

    private static int Main(string[] args)
    {
        var arm = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        if (arm is not ("rate" or "stall" or "pan" or "all"))
        {
            Console.WriteLine("usage: ToupcamVideoProbe rate|stall|pan|all [--seconds N] [--cycles N]");
            return 1;
        }

        var seconds = IntOption(args, "--seconds", 60);
        var cycles = IntOption(args, "--cycles", 20);

        Console.WriteLine($"ToupcamVideoProbe {arm}, {DateTime.Now:yyyy-MM-dd HH:mm}, SDK {ToupcamBrand.ToupTek.Api?.SdkVersion ?? "(not loaded)"}");
        if (Find() is not { } found)
        {
            Console.WriteLine("No ToupTek camera found.");
            return 2;
        }

        Console.WriteLine($"Camera {found.Name} ({found.Brand?.Name}), flags 0x{found.Model.Flag:X16}, model MaxSpeed {found.Model.MaxSpeed}");

        var ok = true;
        if (arm is "rate" or "all")
        {
            ok &= RateArm(TimeSpan.FromSeconds(seconds));
        }

        if (arm is "pan" or "all")
        {
            ok &= PanArm();
        }

        if (arm is "stall" or "all")
        {
            ok &= StallArm(cycles);
        }

        Console.WriteLine(ok ? "ALL PASSED" : "SOME CHECKS FAILED");
        return ok ? 0 : 1;
    }

    // ---- Rate ----------------------------------------------------------------------------------------

    /// <summary>
    /// Three 60 s runs at the Saturn region in 8 bits at 5 ms, each on a fresh open, then one in 16 bits
    /// and one at 2 ms. A run passes at 190 frames a second or more (90 in 16 bits) with nothing dropped
    /// and no stall.
    /// </summary>
    private static bool RateArm(TimeSpan duration)
    {
        Console.WriteLine();
        Console.WriteLine($"== rate: {RoiWidth}x{RoiHeight} at ({RoiX}, {RoiY}), gain {Gain}, {duration.TotalSeconds:F0} s per run after 1 s settling, each run on a fresh open");
        var ok = true;
        (string Label, bool Bits16, int ExposureUs, double MinFps)[] runs =
        [
            ("8-bit, 5 ms", false, 5000, 190),
            ("8-bit, 5 ms", false, 5000, 190),
            ("8-bit, 5 ms", false, 5000, 190),
            ("16-bit, 5 ms", true, 5000, 90),
            ("8-bit, 2 ms", false, 2000, 190),
        ];

        for (var i = 0; i < runs.Length; i++)
        {
            var run = runs[i];
            var camera = OpenFresh();
            try
            {
                Configure(camera, run.Bits16, run.ExposureUs, RoiX, RoiY);
                var result = Stream(camera, run.Bits16, TimeSpan.FromSeconds(1), duration);
                var passed = result.Stall is null && result.Fps >= run.MinFps && result.Dropped == 0;
                ok &= passed;
                Console.WriteLine($"run {i + 1}: {run.Label}: {result.Frames} frames in {result.Seconds:F2} s = {result.Fps:F1} fps by Stopwatch; "
                    + $"get_FrameRate {Format(result.SdkFps)}; dropped {Format(result.Dropped)}; stalls {(result.Stall is null ? 0 : 1)}{Describe(result)}; "
                    + $"timeouts {result.Timeouts}; mean {result.MeanAdu:F1} ADU; stop {result.StopCode} in {result.StopMs:F0} ms  {(passed ? "PASS" : "FAIL")}");
            }
            finally
            {
                _ = camera.Close();
            }
        }

        return ok;
    }

    // ---- Stall ---------------------------------------------------------------------------------------

    /// <summary>
    /// Stop, reconfigure and start on ONE handle, <paramref name="cycles"/> times, alternating the depth
    /// and moving the window, 3 s of stream each. A stall must come back from <c>GetVideoData</c> as an
    /// error within about 2 s of the last frame, and <c>ResetDevice</c> then a fresh open must stream
    /// again. One deliberate reset follows whether or not anything stalled, so the recovery path is
    /// always exercised, and then the single exposures straight after a stream.
    /// </summary>
    /// <remarks>
    /// A stall that begins in the last second of a cycle is caught by the stop instead, before the
    /// SDK's no-packet timeout fires: the switch back to trigger mode then blocks about 5 s. Such a
    /// stop is counted apart, as "caught by the stop".
    /// </remarks>
    private static bool StallArm(int cycles)
    {
        Console.WriteLine();
        Console.WriteLine($"== stall: {cycles} stop / reconfigure / start cycles on one handle, 3 s each, 5 ms, depth alternating, window stepping 64 px");
        var ok = true;
        int stalls = 0, surfaced = 0, silent = 0, recovered = 0, caughtByStop = 0;
        var worstLatency = 0d;
        var camera = OpenFresh();
        try
        {
            for (var cycle = 1; cycle <= cycles; cycle++)
            {
                var bits16 = cycle % 2 == 0;
                var x = RoiX + cycle % 4 * 64;
                Configure(camera, bits16, 5000, x, RoiY);
                var result = Stream(camera, bits16, TimeSpan.Zero, TimeSpan.FromSeconds(3));
                var line = $"cycle {cycle,2}: {(bits16 ? 16 : 8),2}-bit at ({x}, {RoiY}): {result.Frames} frames, {result.Fps:F1} fps, dropped {Format(result.Dropped)}, stop {result.StopCode} in {result.StopMs:F0} ms";
                if (result.Stall is null)
                {
                    if (result.StopMs > 2000)
                    {
                        caughtByStop++;
                        line += "; the stop blocked, so the stream had stalled at its end";
                    }

                    Console.WriteLine(line);
                    continue;
                }

                stalls++;
                if (result.Stall is "silent")
                {
                    silent++;
                }
                else
                {
                    surfaced++;
                    worstLatency = Math.Max(worstLatency, result.StallLatency);
                }

                Console.WriteLine($"{line}; STALL{Describe(result)}");
                camera = ResetAndReopen(camera, out var streamedAgain);
                if (streamedAgain)
                {
                    recovered++;
                }
            }

            var stallsOk = silent == 0 && recovered == stalls && worstLatency <= 2.5;
            ok &= stallsOk;
            Console.WriteLine($"stall summary: {cycles} cycles, {stalls} stalls, {surfaced} surfaced as an error (worst {worstLatency:F2} s after the last frame), "
                + $"{silent} silent, {caughtByStop} caught by the stop, {recovered} of {stalls} streaming again after ResetDevice and a fresh open  {(stallsOk ? "PASS" : "FAIL")}");

            Console.WriteLine("deliberate reset:");
            camera = ResetAndReopen(camera, out var streamsAfterReset);
            ok &= streamsAfterReset;

            ok &= SingleExposureAfterStream(camera);
        }
        finally
        {
            _ = camera.Close();
        }

        return ok;
    }

    /// <summary>Resets the camera, waits for it to come back, opens it afresh and streams 3 s.</summary>
    private static ToupcamCamera ResetAndReopen(ToupcamCamera camera, out bool streamedAgain)
    {
        var clock = Stopwatch.StartNew();
        var reset = camera.ResetDevice();
        ToupcamCamera? back = null;
        while (clock.Elapsed < ReappearTimeout)
        {
            Thread.Sleep(200);
            if (Find() is { } found && found.Open())
            {
                back = found;
                break;
            }
        }

        if (back is not { } reopened)
        {
            Console.WriteLine($"  ResetDevice {reset}; the camera did NOT come back within {ReappearTimeout.TotalSeconds:F0} s");
            throw new InvalidOperationException("camera lost after a reset");
        }

        var reappeared = clock.Elapsed.TotalSeconds;
        Configure(reopened, false, 5000, RoiX, RoiY);
        var result = Stream(reopened, false, TimeSpan.Zero, TimeSpan.FromSeconds(3));
        streamedAgain = result.Stall is null && result.Frames > 0 && result.Dropped is not null;
        Console.WriteLine($"  ResetDevice {reset}; open again after {reappeared:F1} s; fresh stream {result.Frames} frames, {result.Fps:F1} fps, "
            + $"dropped {Format(result.Dropped)}{(result.Stall is null ? "" : "; STALLED AGAIN" + Describe(result))}");
        return reopened;
    }

    /// <summary>
    /// A stream, its stop, and at once two 200 ms single exposures at the highest gain. Each must take
    /// at least its exposure to arrive, and the two must agree: a video frame left over from the
    /// stream and read as the first exposure looks like the stream's (5 ms, gain 2252), not like the
    /// second exposure.
    /// </summary>
    private static bool SingleExposureAfterStream(ToupcamCamera camera)
    {
        Configure(camera, false, 5000, RoiX, RoiY);
        var stream = Stream(camera, false, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        Check(camera.SetControlValue(CMOSControlType.Exposure, 200_000), "set a 200 ms exposure");
        // The highest gain, so a capped camera still tells the frames apart: its read noise lifts the
        // brightest pixels far above the stream's, where 200 ms of dark current alone does not.
        if (camera.TryGetControlRange(CMOSControlType.Gain, out _, out var highestGain))
        {
            Check(camera.SetControlValue(CMOSControlType.Gain, highestGain), "set the highest gain");
        }

        var first = Expose(camera);
        var second = Expose(camera);
        var agree = Math.Abs(first.Bright - second.Bright) <= 0.15 * second.Bright + 1;
        // Even capped, the hot pixels integrate: the brightest 0.1 percent of a 200 ms frame reads far
        // above a 5 ms one. Where it does not, the comparison cannot tell a leftover frame from a real
        // one, and says so.
        var telling = second.Bright > 2 * stream.Bright + 2;
        var passed = stream.Stall is null && first.Ok && second.Ok && agree;
        Console.WriteLine($"single exposures after a stream ({stream.Frames} frames, brightest 0.1 % at {stream.Bright:F1} ADU, stop {stream.StopCode} in {stream.StopMs:F0} ms): "
            + $"first {first.What}, second {second.What}; {(agree ? "they agree" : "they DIFFER, so the first was not its own exposure")}"
            + $"{(telling ? "" : " (inconclusive: 200 ms reads like 5 ms here)")}  {(passed ? "PASS" : "FAIL")}");
        return passed;
    }

    /// <summary>One single exposure at the exposure set now: started, awaited, read, and its mean.</summary>
    private static (bool Ok, double Bright, string What) Expose(ToupcamCamera camera)
    {
        var clock = Stopwatch.StartNew();
        var started = camera.StartLightExposure();
        var status = ExposureStatus.Working;
        while (started is CMOSErrorCode.Success && status is ExposureStatus.Working && clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            Thread.Sleep(1);
            _ = camera.GetExposureStatus(out status);
        }

        var took = clock.Elapsed.TotalSeconds;
        var buffer = Marshal.AllocHGlobal(RoiWidth * RoiHeight);
        try
        {
            var read = status is ExposureStatus.Success ? camera.GetDataAfterExposure(buffer, RoiWidth * RoiHeight) : CMOSErrorCode.GeneralError;
            var mean = read is CMOSErrorCode.Success ? MeanAdu(buffer, false) : double.NaN;
            var bright = read is CMOSErrorCode.Success ? BrightestAdu(buffer) : double.NaN;
            var ok = started is CMOSErrorCode.Success && read is CMOSErrorCode.Success && took >= 0.2;
            return (ok, bright, $"{(ok ? "" : $"start {started} status {status} read {read} ")}{took * 1000:F0} ms, mean {mean:F1}, brightest 0.1 % {bright:F1} ADU");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ---- Pan -----------------------------------------------------------------------------------------

    /// <summary>
    /// Moves the window by 64 px while the stream runs, ten times back and forth, and measures whether
    /// the picture moved by that much, whether the stream paused (a restart shows as a gap of many
    /// frame times) and whether it stalled.
    /// </summary>
    private static bool PanArm()
    {
        Console.WriteLine();
        var camera = OpenFresh();
        Console.WriteLine($"== pan: SetStartPosition by 64 px mid-stream, 8-bit, 5 ms; CanPanRoiWhileStreaming = {camera.CanPanRoiWhileStreaming}");
        var frameBytes = RoiWidth * RoiHeight;
        var buffer = Marshal.AllocHGlobal(frameBytes);
        try
        {
            Configure(camera, false, 5000, RoiX, RoiY);
            Check(camera.StartVideoCapture(), "start the stream");
            var clock = Stopwatch.StartNew();
            _ = ReadFor(camera, buffer, frameBytes, clock, TimeSpan.FromSeconds(1), null);
            var x = RoiX;
            var ok = true;
            for (var pan = 1; pan <= 10; pan++)
            {
                var before = Average(camera, buffer, frameBytes, 16);
                var dx = pan % 2 == 1 ? 64 : -64;
                var calledAt = clock.Elapsed;
                var code = camera.SetStartPosition(x + dx, RoiY);
                var callMs = (clock.Elapsed - calledAt).TotalMilliseconds;
                if (code is not CMOSErrorCode.Success)
                {
                    Console.WriteLine($"pan {pan}: SetStartPosition({x + dx}, {RoiY}) answered {code}; panning mid-stream is refused");
                    ok = camera.CanPanRoiWhileStreaming is false && code is CMOSErrorCode.VideoModeActive;
                    break;
                }

                x += dx;
                var arrivals = new List<double>();
                var after = ReadFor(camera, buffer, frameBytes, clock, TimeSpan.FromMilliseconds(500), arrivals);
                var later = Average(camera, buffer, frameBytes, 16);
                var (shift, contrast) = BestShift(before, later);
                var gaps = arrivals.Zip(arrivals.Skip(1), (a, b) => b - a).ToList();
                var firstGap = arrivals.Count > 0 ? arrivals[0] - calledAt.TotalMilliseconds : double.NaN;
                var maxGap = gaps.Count > 0 ? Math.Max(gaps.Max(), firstGap) : double.NaN;
                var median = gaps.Count > 0 ? gaps.Order().ElementAt(gaps.Count / 2) : double.NaN;
                _ = camera.TryGetDroppedFrames(out var dropped);
                var moved = shift == dx;
                ok &= after is null && moved && maxGap < 50;
                Console.WriteLine($"pan {pan}: to ({x}, {RoiY}) by {dx,3}: call {callMs:F1} ms; {arrivals.Count} frames in the next 500 ms, first after {firstGap:F1} ms, "
                    + $"longest gap {maxGap:F1} ms (median {median:F1}); picture moved {shift,4} px (match contrast {contrast:F2}); "
                    + $"dropped so far {dropped}{(after is null ? "" : "; STREAM ENDED " + after)}");
                if (after is not null)
                {
                    break;
                }
            }

            var stopped = camera.StopVideoCapture();
            Console.WriteLine($"pan summary: {(ok ? "the window moves mid-stream with no restart and no stall" : "panning mid-stream does NOT hold")}; stop {stopped}");
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            _ = camera.Close();
        }
    }

    /// <summary>Reads frames for <paramref name="span"/>, noting each arrival; the error that ended the
    /// stream, or null.</summary>
    private static CMOSErrorCode? ReadFor(ToupcamCamera camera, nint buffer, int frameBytes, Stopwatch clock, TimeSpan span, List<double>? arrivals)
    {
        var until = clock.Elapsed + span;
        while (clock.Elapsed < until)
        {
            var code = camera.GetVideoData(buffer, frameBytes, FrameWaitMs);
            if (code is CMOSErrorCode.Success)
            {
                arrivals?.Add(clock.Elapsed.TotalMilliseconds);
            }
            else if (code is not CMOSErrorCode.Timeout)
            {
                return code;
            }
        }

        return null;
    }

    /// <summary>The mean of the next <paramref name="count"/> 8-bit frames, per pixel.</summary>
    private static unsafe float[] Average(ToupcamCamera camera, nint buffer, int frameBytes, int count)
    {
        var sum = new float[frameBytes];
        var taken = 0;
        while (taken < count)
        {
            var code = camera.GetVideoData(buffer, frameBytes, FrameWaitMs);
            if (code is CMOSErrorCode.Timeout)
            {
                continue;
            }

            Check(code, "read a frame to average");
            var pixels = new ReadOnlySpan<byte>((void*)buffer, frameBytes);
            for (var i = 0; i < frameBytes; i++)
            {
                sum[i] += pixels[i];
            }

            taken++;
        }

        for (var i = 0; i < frameBytes; i++)
        {
            sum[i] /= count;
        }

        return sum;
    }

    /// <summary>
    /// The horizontal shift s (even, within 128 px) that best maps <paramref name="later"/> onto
    /// <paramref name="before"/> as later(x) = before(x + s), and how distinct that match is: the
    /// median difference over all shifts divided by the best, so 1 means the picture has no structure
    /// to match and the shift means nothing.
    /// </summary>
    private static (int Shift, double Contrast) BestShift(float[] before, float[] later)
    {
        var scores = new List<(int Shift, double Score)>();
        for (var shift = -128; shift <= 128; shift += 2)
        {
            double total = 0;
            long n = 0;
            for (var y = 0; y < RoiHeight; y += 4)
            {
                var row = y * RoiWidth;
                for (var x = Math.Max(0, -shift); x < Math.Min(RoiWidth, RoiWidth - shift); x += 2)
                {
                    total += Math.Abs(later[row + x] - before[row + x + shift]);
                    n++;
                }
            }

            scores.Add((shift, total / n));
        }

        var best = scores.MinBy(s => s.Score);
        var median = scores.Select(s => s.Score).Order().ElementAt(scores.Count / 2);
        return (best.Shift, best.Score > 0 ? median / best.Score : double.PositiveInfinity);
    }

    // ---- Streaming, as TianWen's loop does it ---------------------------------------------------------

    private sealed record StreamResult(
        long Frames,
        double Seconds,
        long Timeouts,
        int? Dropped,
        double? SdkFps,
        string? Stall,
        double StallLatency,
        double MeanAdu,
        double Bright,
        CMOSErrorCode StopCode,
        double StopMs)
    {
        public double Fps => Seconds > 0 ? Frames / Seconds : 0;
    }

    /// <summary>
    /// Starts the stream, reads it for <paramref name="settle"/> uncounted and then
    /// <paramref name="span"/> counted, with TianWen's 250 ms wait, and stops it. A stall is any answer
    /// but Success and Timeout, or nothing at all for <see cref="SilentStallAfter"/>.
    /// </summary>
    private static StreamResult Stream(ToupcamCamera camera, bool bits16, TimeSpan settle, TimeSpan span)
    {
        var frameBytes = RoiWidth * RoiHeight * (bits16 ? 2 : 1);
        var buffer = Marshal.AllocHGlobal(frameBytes);
        try
        {
            Check(camera.StartVideoCapture(), "start the stream");
            var clock = Stopwatch.StartNew();
            var lastFrame = TimeSpan.Zero;
            long frames = 0, timeouts = 0;
            string? stall = null;
            var latency = 0d;
            var end = settle + span;
            while (clock.Elapsed < end)
            {
                var code = camera.GetVideoData(buffer, frameBytes, FrameWaitMs);
                var now = clock.Elapsed;
                if (code is CMOSErrorCode.Success)
                {
                    lastFrame = now;
                    if (now >= settle)
                    {
                        frames++;
                    }
                }
                else if (code is CMOSErrorCode.Timeout)
                {
                    timeouts++;
                    if (now - lastFrame >= SilentStallAfter)
                    {
                        stall = "silent";
                        latency = (now - lastFrame).TotalSeconds;
                        break;
                    }
                }
                else
                {
                    stall = code.ToString();
                    latency = (now - lastFrame).TotalSeconds;
                    break;
                }
            }

            var counted = (Math.Min(clock.Elapsed.TotalSeconds, end.TotalSeconds) - settle.TotalSeconds);
            int? dropped = camera.TryGetDroppedFrames(out var d) ? d : null;
            var sdkFps = camera.SdkFrameRate;
            var mean = frames > 0 ? MeanAdu(buffer, bits16) : double.NaN;
            var bright = frames > 0 && !bits16 ? BrightestAdu(buffer) : double.NaN;
            var stopClock = Stopwatch.StartNew();
            var stopped = camera.StopVideoCapture();
            return new StreamResult(frames, Math.Max(0, counted), timeouts, dropped, sdkFps, stall, latency, mean, bright, stopped, stopClock.Elapsed.TotalMilliseconds);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void Configure(ToupcamCamera camera, bool bits16, int exposureUs, int x, int y)
    {
        Check(camera.SetROIFormat(RoiWidth, RoiHeight, 1, bits16 ? PixelDataFormat.RAW16 : PixelDataFormat.RAW8), "set the window size and format");
        Check(camera.SetStartPosition(x, y), "set the window start");
        Check(camera.SetControlValue(CMOSControlType.Exposure, exposureUs), "set the exposure");
        Check(camera.SetControlValue(CMOSControlType.Gain, Gain), "set the gain");
        // TianWen's video loop takes the whole bandwidth, which on this binding is the top speed level.
        if (camera.TryGetControlRange(CMOSControlType.BandwidthOverload, out _, out var most))
        {
            Check(camera.SetControlValue(CMOSControlType.BandwidthOverload, most), "set the speed level");
        }
    }

    private static ToupcamCamera? Find()
    {
        foreach (var camera in new DeviceIterator())
        {
            if (camera.Brand == ToupcamBrand.ToupTek)
            {
                return camera;
            }
        }

        return null;
    }

    private static ToupcamCamera OpenFresh()
    {
        if (Find() is { } camera && camera.Open())
        {
            return camera;
        }

        throw new InvalidOperationException("could not open the ToupTek camera");
    }

    private static unsafe double MeanAdu(nint buffer, bool bits16)
    {
        double sum = 0;
        var count = RoiWidth * RoiHeight;
        if (bits16)
        {
            var pixels = new ReadOnlySpan<ushort>((void*)buffer, count);
            foreach (var p in pixels)
            {
                sum += p;
            }
        }
        else
        {
            var pixels = new ReadOnlySpan<byte>((void*)buffer, count);
            foreach (var p in pixels)
            {
                sum += p;
            }
        }

        return sum / count;
    }

    /// <summary>The mean of the brightest 0.1 percent of an 8-bit frame: its hot pixels, when capped.</summary>
    private static unsafe double BrightestAdu(nint buffer)
    {
        var histogram = new long[256];
        var count = RoiWidth * RoiHeight;
        foreach (var p in new ReadOnlySpan<byte>((void*)buffer, count))
        {
            histogram[p]++;
        }

        var wanted = Math.Max(1, count / 1000);
        long taken = 0;
        double sum = 0;
        for (var value = 255; value >= 0 && taken < wanted; value--)
        {
            var take = Math.Min(histogram[value], wanted - taken);
            sum += take * value;
            taken += take;
        }

        return sum / taken;
    }

    private static void Check(CMOSErrorCode code, string what)
    {
        if (code is not CMOSErrorCode.Success)
        {
            throw new InvalidOperationException($"could not {what}: {code}");
        }
    }

    private static string Describe(StreamResult result)
        => result.Stall is null ? "" : $" ({result.Stall}, {result.StallLatency:F2} s after the last frame)";

    private static string Format(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "n/a";

    private static string Format(double? value) => value is { } v ? v.ToString("F1", CultureInfo.InvariantCulture) : "n/a";

    private static int IntOption(string[] args, string name, int fallback)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out var value) ? value : fallback;
    }
}
