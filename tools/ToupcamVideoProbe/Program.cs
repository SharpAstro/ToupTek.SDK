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

    /// <summary>Time since the probe last reset the camera, for the soak's stall lines.</summary>
    private static readonly Stopwatch SinceReset = new Stopwatch();

    /// <summary>Whether a stall is first nudged (the speed level, then the window) before the stream is
    /// stopped: the recover arm's question.</summary>
    private static bool TryNudges;

    private static int Main(string[] args)
    {
        var arm = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        if (arm is not ("rate" or "stall" or "pan" or "all" or "soak" or "speedstep" or "recover"))
        {
            Console.WriteLine("usage: ToupcamVideoProbe rate|stall|pan|all|soak|speedstep|recover [--seconds N] [--cycles N] [--rounds N] [--segment S]");
            return 1;
        }

        var seconds = IntOption(args, "--seconds", 60);
        var cycles = IntOption(args, "--cycles", 20);
        var rounds = IntOption(args, "--rounds", 4);
        var segment = IntOption(args, "--segment", 60);

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

        if (arm is "speedstep")
        {
            ok &= SpeedStepArm();
        }

        if (arm is "soak" or "recover")
        {
            ok &= SoakArm(rounds, TimeSpan.FromSeconds(segment), recover: arm is "recover");
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
        // At the highest gain the brightest 0.1 percent (read noise and hot pixels) reads far above the
        // stream's at 2252, so a leftover stream frame cannot pass for a single exposure. Where it does
        // not, the comparison cannot tell them apart, and says so rather than passing.
        var telling = second.Bright > 2 * stream.Bright + 2;
        var passed = first.Ok && second.Ok && agree && telling;
        Console.WriteLine($"single exposures after a stream ({stream.Frames} frames, brightest 0.1 % at {stream.Bright:F1} ADU, "
            + $"{(stream.Stall is null ? "" : "the stream STALLED" + Describe(stream) + ", ")}stop {stream.StopCode} in {stream.StopMs:F0} ms): "
            + $"first {first.What}, second {second.What}; {(agree ? "they agree" : "they DIFFER, so the first was not its own exposure")}"
            + $"{(telling ? "" : "; INCONCLUSIVE, the exposures read like the stream here")}  {(passed ? "PASS" : "FAIL")}");
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
    /// Moves the window by 64 px while the stream runs, ten times back and forth. A pan holds when the
    /// picture moved by exactly that much, the camera lost at most one frame to it (its own frame
    /// count, through <c>TryGetDroppedFrames</c>; a restart would lose the 40 or so of its 200 ms),
    /// and the stream did not stall.
    /// </summary>
    /// <remarks>
    /// How long <c>SetStartPosition</c> blocks its caller, and the longest wait between two frames as
    /// the reader saw them, are printed but not judged: the stream runs on while the call blocks, its
    /// frames queuing in the ring, so the reader's gap measures the call rather than the camera.
    /// </remarks>
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
                _ = camera.TryGetDroppedFrames(out var droppedBefore);
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
                var counted = camera.TryGetDroppedFrames(out var droppedAfter);
                var lost = droppedAfter - droppedBefore;
                var moved = shift == dx;
                ok &= after is null && moved && counted && lost <= 1;
                Console.WriteLine($"pan {pan}: to ({x}, {RoiY}) by {dx,3}: picture moved {shift,4} px (match contrast {contrast:F2}); camera lost {(counted ? lost.ToString(CultureInfo.InvariantCulture) : "n/a")} frame(s); "
                    + $"call {callMs:F1} ms; reader: {arrivals.Count} frames in the next 500 ms, first after {firstGap:F1} ms, longest gap {maxGap:F1} ms (median {median:F1})"
                    + $"{(after is null ? "" : "; STREAM ENDED " + after)}");
                if (after is not null)
                {
                    break;
                }
            }

            var stopped = camera.StopVideoCapture();
            Console.WriteLine($"pan summary: {(ok ? "the window moves mid-stream with no restart and no stall" : "panning mid-stream does NOT hold")}; stop {stopped}  {(ok ? "PASS" : "FAIL")}");
            return ok;
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

    // ---- Speed step ----------------------------------------------------------------------------------

    /// <summary>
    /// Changes the speed level of a running 5 ms stream eight times and measures what each change
    /// costs: how long the call blocks, how long the frames stop, how many the camera loses, the rate a
    /// second later, and any stall. An adaptive bandwidth wants this cheap, and without a restart.
    /// </summary>
    private static bool SpeedStepArm()
    {
        Console.WriteLine();
        Console.WriteLine("== speedstep: BandwidthOverload (the speed level) changed on a running stream, 8-bit, 5 ms, 2 s per level");
        var camera = OpenFresh();
        var frameBytes = RoiWidth * RoiHeight;
        var buffer = Marshal.AllocHGlobal(frameBytes);
        var ok = true;
        try
        {
            Configure(camera, false, 5000, RoiX, RoiY);
            Check(camera.StartVideoCapture(), "start the stream");
            var clock = Stopwatch.StartNew();
            var settled = ReadFor(camera, buffer, frameBytes, clock, TimeSpan.FromSeconds(2), null);
            if (settled is not null)
            {
                Console.WriteLine($"the stream ended before the first step: {settled}");
                return false;
            }

            foreach (var level in new[] { 1, 2, 0, 2, 1, 0, 1, 2 })
            {
                _ = camera.TryGetDroppedFrames(out var droppedBefore);
                var arrivals = new List<double>();
                var calledAt = clock.Elapsed.TotalMilliseconds;
                var code = camera.SetControlValue(CMOSControlType.BandwidthOverload, level);
                var callMs = clock.Elapsed.TotalMilliseconds - calledAt;
                var ended = ReadFor(camera, buffer, frameBytes, clock, TimeSpan.FromSeconds(2), arrivals);
                var counted = camera.TryGetDroppedFrames(out var droppedAfter);
                var firstAfter = arrivals.Count > 0 ? arrivals[0] - calledAt : double.NaN;
                var gaps = arrivals.Zip(arrivals.Skip(1), (a, b) => b - a).ToList();
                var longest = gaps.Count > 0 ? Math.Max(gaps.Max(), firstAfter) : double.NaN;
                var settledFps = arrivals.Count(t => t - calledAt >= 1000);
                var held = code is CMOSErrorCode.Success && ended is null;
                ok &= held;
                Console.WriteLine($"to level {level}: {code} in {callMs:F1} ms; first frame after {firstAfter:F1} ms, longest gap {longest:F1} ms; "
                    + $"{settledFps} fps in the second after; camera lost {(counted ? (droppedAfter - droppedBefore).ToString(CultureInfo.InvariantCulture) : "n/a")}"
                    + $"{(ended is null ? "" : "; STREAM ENDED " + ended)}");
                if (ended is not null)
                {
                    break;
                }
            }

            _ = camera.StopVideoCapture();
            Console.WriteLine($"speedstep summary: {(ok ? "the speed level changes on a running stream" : "a change did NOT hold")}  {(ok ? "PASS" : "FAIL")}");
            return ok;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            _ = camera.Close();
        }
    }

    // ---- Soak ----------------------------------------------------------------------------------------

    /// <summary>One cell of the soak matrix and everything measured in it.</summary>
    private sealed class SoakCell(int exposureUs, int speed)
    {
        public int ExposureUs { get; } = exposureUs;
        public int Speed { get; } = speed;
        public string Label => $"{ExposureUs / 1000.0,4:0.#} ms, speed {Speed}";
        public double Seconds;
        public long Frames;
        public long Dropped;
        public int UnknownDrops;
        public int Stalls;
        public int Warned;
        public int SpeedNudged;
        public int WindowNudged;
        public int Restarted;
        public int Reopened;
        public int Reset;
        public double RecoverySeconds;
    }

    /// <summary>
    /// Every exposure (5, 15 and 2 ms) at every speed level, 8 bits at the Saturn window, in
    /// <paramref name="segment"/>-long segments taken round-robin for <paramref name="rounds"/> rounds,
    /// each round starting one cell later, so that every cell samples the camera's good and bad spells
    /// alike. It starts from a reset. A stall is recovered by the cheapest step that works (the stream
    /// stopped and started on the same handle, then a close and open, then <c>ResetDevice</c>), each
    /// timed from the last frame to the first again; per stall, the frames dropped in the 10 s before it
    /// say whether drops warn of one.
    /// </summary>
    /// <remarks>
    /// With <paramref name="recover"/> it is the recover arm instead: 5 ms only, at the top speed level
    /// and the one below it, and a stall is first nudged, the speed level set to its neighbour and back
    /// and then the window moved 2 px and back, each given 2 s to bring frames back, before the stream is
    /// stopped. The question is whether a stall can be undone without the stop, which blocks about 5 s
    /// on a stalled camera.
    /// </remarks>
    private static bool SoakArm(int rounds, TimeSpan segment, bool recover)
    {
        TryNudges = recover;
        Console.WriteLine();
        Console.WriteLine(recover
            ? $"== recover: 8-bit {RoiWidth}x{RoiHeight} at ({RoiX}, {RoiY}), gain {Gain}, 5 ms at the top two speed levels, "
                + $"{segment.TotalSeconds:F0} s segments alternating, {rounds} rounds, from a reset; a stall is nudged before it is stopped"
            : $"== soak: 8-bit {RoiWidth}x{RoiHeight} at ({RoiX}, {RoiY}), gain {Gain}; every exposure at every speed level, "
                + $"{segment.TotalSeconds:F0} s segments round-robin, {rounds} rounds, from a reset");
        var camera = ResetOnly(OpenFresh());
        Console.WriteLine($"reset at the start, open again after {SinceReset.Elapsed.TotalSeconds:F1} s");
        var speeds = camera.TryGetControlRange(CMOSControlType.BandwidthOverload, out var lowest, out var highest)
            ? Enumerable.Range(lowest, highest - lowest + 1).Reverse().ToArray()
            : [0];
        var cells = recover
            ? speeds.Take(2).Select(speed => new SoakCell(5000, speed)).ToList()
            : new[] { 5000, 15000, 2000 }.SelectMany(exposure => speeds.Select(speed => new SoakCell(exposure, speed))).ToList();
        var completed = true;
        try
        {
            for (var round = 0; round < rounds; round++)
            {
                for (var i = 0; i < cells.Count; i++)
                {
                    camera = SoakSegment(camera, cells[(i + round) % cells.Count], segment, round + 1);
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            completed = false;
            Console.WriteLine($"SOAK ABORTED: {ex.Message}");
        }
        finally
        {
            _ = camera.Close();
        }

        Console.WriteLine();
        Console.WriteLine("| Exposure, speed | Streamed | fps | Delivered fps | Dropped | 1 in | Stalls | Per hour | Drops in the 10 s before | Speed nudge | Window nudge | Restart | Reopen | Reset | Mean recovery |");
        Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var cell in cells)
        {
            var oneIn = cell.Dropped > 0 ? (cell.Frames / cell.Dropped).ToString(CultureInfo.InvariantCulture) : "none";
            var wall = cell.Seconds + cell.RecoverySeconds;
            Console.WriteLine($"| {cell.Label} | {cell.Seconds:F0} s | {(cell.Seconds > 0 ? cell.Frames / cell.Seconds : 0):F1} | {(wall > 0 ? cell.Frames / wall : 0):F1} "
                + $"| {cell.Dropped}{(cell.UnknownDrops > 0 ? $" (+{cell.UnknownDrops} unknown)" : "")} "
                + $"| {oneIn} | {cell.Stalls} | {(cell.Seconds > 0 ? cell.Stalls * 3600 / cell.Seconds : 0):F0} | {cell.Warned} of {cell.Stalls} "
                + $"| {cell.SpeedNudged} | {cell.WindowNudged} | {cell.Restarted} | {cell.Reopened} | {cell.Reset} | {(cell.Stalls > 0 ? cell.RecoverySeconds / cell.Stalls : 0):F1} s |");
        }

        return completed;
    }

    /// <summary>One segment of one cell: configure, stream, recover from any stall, stop.</summary>
    private static ToupcamCamera SoakSegment(ToupcamCamera camera, SoakCell cell, TimeSpan span, int round)
    {
        var frameBytes = RoiWidth * RoiHeight;
        var buffer = Marshal.AllocHGlobal(frameBytes);
        try
        {
            Configure(camera, false, cell.ExposureUs, RoiX, RoiY, cell.Speed);
            Check(camera.StartVideoCapture(), "start the stream");
            var clock = Stopwatch.StartNew();
            var lastFrame = TimeSpan.Zero;
            var recovering = TimeSpan.Zero;
            long frames = 0, dropped = 0;
            var lines = new List<string>();
            // The stream's dropped count once a second, so a stall can say what came in the 10 s before it.
            var samples = new Queue<(TimeSpan At, int Dropped)>();
            var nextSample = TimeSpan.Zero;
            while (clock.Elapsed < span)
            {
                var code = camera.GetVideoData(buffer, frameBytes, FrameWaitMs);
                var now = clock.Elapsed;
                if (code is CMOSErrorCode.Success)
                {
                    frames++;
                    lastFrame = now;
                    if (now >= nextSample)
                    {
                        samples.Enqueue((now, camera.TryGetDroppedFrames(out var d) ? d : -1));
                        while (now - samples.Peek().At > TimeSpan.FromSeconds(10.5))
                        {
                            _ = samples.Dequeue();
                        }

                        nextSample = now + TimeSpan.FromSeconds(1);
                    }

                    continue;
                }

                if (code is CMOSErrorCode.Timeout && now - lastFrame < SilentStallAfter)
                {
                    continue;
                }

                var latency = now - lastFrame;
                var streamDropped = camera.TryGetDroppedFrames(out var sd) ? sd : -1;
                var before = streamDropped >= 0 && samples.Count > 0 && samples.Peek().Dropped >= 0 ? streamDropped - samples.Peek().Dropped : -1;
                if (streamDropped >= 0)
                {
                    dropped += streamDropped;
                }
                else
                {
                    cell.UnknownDrops++;
                }

                cell.Stalls++;
                if (before > 0)
                {
                    cell.Warned++;
                }

                var minutesSinceReset = SinceReset.Elapsed.TotalMinutes;
                var recoveryStarted = clock.Elapsed;
                camera = Recover(camera, cell, buffer, frameBytes, out var how);
                var gap = clock.Elapsed - recoveryStarted + latency;
                recovering += gap;
                cell.RecoverySeconds += gap.TotalSeconds;
                lines.Add($"  stall at {now.TotalSeconds:F1} s ({(code is CMOSErrorCode.Timeout ? "silent" : code.ToString())}, {latency.TotalSeconds:F2} s after the last frame, "
                    + $"{minutesSinceReset:F1} min after the last reset, {(before >= 0 ? before.ToString(CultureInfo.InvariantCulture) : "n/a")} dropped in the 10 s before): "
                    + $"{how}, streaming again {gap.TotalSeconds:F1} s after the last frame");
                lastFrame = clock.Elapsed;
                samples.Clear();
                nextSample = TimeSpan.Zero;
            }

            if (camera.TryGetDroppedFrames(out var tail))
            {
                dropped += tail;
            }
            else
            {
                cell.UnknownDrops++;
            }

            var streamed = (clock.Elapsed - recovering).TotalSeconds;
            _ = camera.StopVideoCapture();
            cell.Seconds += streamed;
            cell.Frames += frames;
            cell.Dropped += dropped;
            Console.WriteLine($"round {round}, {cell.Label}: {frames} frames in {streamed:F1} s = {(streamed > 0 ? frames / streamed : 0):F1} fps, "
                + $"dropped {dropped}, stalls {lines.Count}, {SinceReset.Elapsed.TotalMinutes:F1} min after the last reset");
            foreach (var line in lines)
            {
                Console.WriteLine(line);
            }

            return camera;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Brings a stalled stream back by the cheapest step that works: stopped and started on the same
    /// handle, then closed and opened again, then reset. Each step must produce a frame within 3 s.
    /// </summary>
    private static ToupcamCamera Recover(ToupcamCamera camera, SoakCell cell, nint buffer, int frameBytes, out string how)
    {
        if (TryNudges)
        {
            // The speed level to its neighbour and back: a change of it takes 9 ms on a healthy stream.
            _ = camera.TryGetControlRange(CMOSControlType.BandwidthOverload, out var lowest, out _);
            _ = camera.SetControlValue(CMOSControlType.BandwidthOverload, cell.Speed > lowest ? cell.Speed - 1 : cell.Speed + 1);
            _ = camera.SetControlValue(CMOSControlType.BandwidthOverload, cell.Speed);
            if (FrameWithin(camera, buffer, frameBytes, TimeSpan.FromSeconds(2)))
            {
                cell.SpeedNudged++;
                how = "speed level nudged" + RestartHealthy(camera);
                return camera;
            }

            // The window 2 px and back: put_Roi on a healthy stream costs one frame.
            _ = camera.SetStartPosition(RoiX + 2, RoiY);
            _ = camera.SetStartPosition(RoiX, RoiY);
            if (FrameWithin(camera, buffer, frameBytes, TimeSpan.FromSeconds(2)))
            {
                cell.WindowNudged++;
                how = "window nudged" + RestartHealthy(camera);
                return camera;
            }
        }

        _ = camera.StopVideoCapture();
        if (camera.StartVideoCapture() is CMOSErrorCode.Success && FirstFrame(camera, buffer, frameBytes))
        {
            cell.Restarted++;
            how = "restarted on the same handle";
            return camera;
        }

        _ = camera.StopVideoCapture();
        _ = camera.Close();
        camera = OpenFresh();
        Configure(camera, false, cell.ExposureUs, RoiX, RoiY, cell.Speed);
        if (camera.StartVideoCapture() is CMOSErrorCode.Success && FirstFrame(camera, buffer, frameBytes))
        {
            cell.Reopened++;
            how = "closed and opened again";
            return camera;
        }

        _ = camera.StopVideoCapture();
        camera = ResetOnly(camera);
        Configure(camera, false, cell.ExposureUs, RoiX, RoiY, cell.Speed);
        if (camera.StartVideoCapture() is CMOSErrorCode.Success && FirstFrame(camera, buffer, frameBytes))
        {
            cell.Reset++;
            how = "reset";
            return camera;
        }

        _ = camera.StopVideoCapture();
        throw new InvalidOperationException($"{cell.Label}: no frame after a restart, a reopen and a reset");
    }

    /// <summary>
    /// Whether a frame arrives within <paramref name="span"/> on a stream the binding has already
    /// called stalled. It goes on answering the stall once its ring is empty, so this keeps asking
    /// rather than taking the first answer.
    /// </summary>
    private static bool FrameWithin(ToupcamCamera camera, nint buffer, int frameBytes, TimeSpan span)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < span)
        {
            if (camera.GetVideoData(buffer, frameBytes, 50) is CMOSErrorCode.Success)
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return false;
    }

    /// <summary>
    /// Stops and starts a stream that has frames again, since the binding keeps answering the stall
    /// it saw until the stream is restarted; says what that cost.
    /// </summary>
    private static string RestartHealthy(ToupcamCamera camera)
    {
        var clock = Stopwatch.StartNew();
        _ = camera.StopVideoCapture();
        var started = camera.StartVideoCapture();
        return $" (then restarted to clear the stall, {started} in {clock.Elapsed.TotalMilliseconds:F0} ms)";
    }

    /// <summary>Waits up to 3 s for a started stream's first frame; false on any other answer.</summary>
    private static bool FirstFrame(ToupcamCamera camera, nint buffer, int frameBytes)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(3))
        {
            var code = camera.GetVideoData(buffer, frameBytes, FrameWaitMs);
            if (code is CMOSErrorCode.Success)
            {
                return true;
            }

            if (code is not CMOSErrorCode.Timeout)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>Resets an open camera, waits for it to come back, and opens it again.</summary>
    private static ToupcamCamera ResetOnly(ToupcamCamera camera)
    {
        var reset = camera.ResetDevice();
        SinceReset.Restart();
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < ReappearTimeout)
        {
            Thread.Sleep(200);
            if (Find() is { } found && found.Open())
            {
                return found;
            }
        }

        throw new InvalidOperationException($"the camera did not come back within {ReappearTimeout.TotalSeconds:F0} s of a reset ({reset})");
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

    /// <summary>The window, depth, exposure and gain, and the speed level: <paramref name="speed"/>, or
    /// the top one as TianWen's video loop takes it (the whole bandwidth) when null.</summary>
    private static void Configure(ToupcamCamera camera, bool bits16, int exposureUs, int x, int y, int? speed = null)
    {
        Check(camera.SetROIFormat(RoiWidth, RoiHeight, 1, bits16 ? PixelDataFormat.RAW16 : PixelDataFormat.RAW8), "set the window size and format");
        Check(camera.SetStartPosition(x, y), "set the window start");
        Check(camera.SetControlValue(CMOSControlType.Exposure, exposureUs), "set the exposure");
        Check(camera.SetControlValue(CMOSControlType.Gain, Gain), "set the gain");
        if (camera.TryGetControlRange(CMOSControlType.BandwidthOverload, out _, out var most))
        {
            Check(camera.SetControlValue(CMOSControlType.BandwidthOverload, speed ?? most), "set the speed level");
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
