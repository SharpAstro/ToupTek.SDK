using System;
using System.Runtime.InteropServices;
using System.Threading;
using TianWen.DAL;
using static ToupTek.SDK.ToupcamConstants;

namespace ToupTek.SDK;

/// <summary>
/// Video: the camera free-running (<c>TOUPCAM_OPTION_TRIGGER = 0</c>), its frames pulled in the event
/// callback into a ring of recycled buffers and handed to <see cref="GetVideoFrame"/> in order.
/// </summary>
/// <remarks>
/// <para><b>The SDK stream is never stopped for video.</b> It runs from open to close in software
/// trigger mode (<see cref="Start"/>), and a video stream only flips <c>TOUPCAM_OPTION_TRIGGER</c> to 0
/// and back, which the manual's option table allows while the camera runs. A <c>Toupcam_Stop</c> and
/// restart on one handle is what set off the stalls measured in the vendor's own video loop (14 of 46
/// runs), so this path does not take one. The switch is not free either: measured on the G3M678M,
/// <c>put_Option(TRIGGER)</c> blocks about 200 ms each way, the SDK evidently restarting the camera's
/// transfer itself, so a start costs about 200 ms and a stop about 300.</para>
/// <para><b>Why the callback pulls, rather than the reader.</b> At 200 frames a second a frame is due
/// every 5 ms, and the DAL's reader runs on its own thread with its own pauses (a decode, a garbage
/// collection). Pulling in <c>TOUPCAM_EVENT_IMAGE</c> empties the SDK's short queues (four raw frames
/// and three processed, by default) as fast as the camera fills them, so a pause above this layer
/// costs nothing until the ring itself is full. The ring is one producer (the SDK's callback thread)
/// and one consumer (the DAL's video thread), so its two indices need no lock between them; the
/// <see cref="_videoGate"/> lock only keeps a pull from writing into a ring that a stop is retiring.</para>
/// <para><b>Nothing is allocated per frame.</b> The ring is allocated when a stream starts and kept
/// for the next one of the same size or smaller; it is freed with the session.</para>
/// </remarks>
internal sealed unsafe partial class ToupcamSession
{
    /// <summary>What the ring may take, all slots together. A 640 x 480 8-bit window fills
    /// <see cref="MaxVideoSlots"/> (320 ms at 200 frames a second) with under a third of it; a full
    /// 16-bit frame gets <see cref="MinVideoSlots"/>.</summary>
    private const long VideoRingBudgetBytes = 64L << 20;
    private const int MinVideoSlots = 4;
    private const int MaxVideoSlots = 64;

    /// <summary>The longest a stop waits for the camera to go quiet after it leaves video mode.</summary>
    private const int MaxVideoQuietMs = 3000;

    /// <summary>A forward jump in the frame sequence larger than this is not frames lost: nothing
    /// streams 2^16 frames in the time between two callbacks.</summary>
    private const uint MaxSequenceGap = 1u << 16;

    /// <summary>How many steps of exactly one the sequence must have taken before a jump in it is
    /// believed to be frames lost.</summary>
    private const int SequenceTrustSteps = 4;

    /// <summary>Frames a stream may take to show a sequence that counts; after that, a stream whose
    /// sequence never did, or which reports none, has its camera-side losses unknown.</summary>
    private const int SequenceTrustWithin = 64;

    /// <summary>Frames in a row whose sequence number is not a count, after which the stream's sequence
    /// is taken as broken rather than as a frame or two with bad info.</summary>
    private const int SequenceGarbageLimit = 8;

    // Held by the callback around a pull into the ring, and by a start or stop around switching it on or
    // off. NEVER held across an SDK call that waits for the callback (Stop, put_Option(TRIGGER)): the
    // manual names exactly that as a deadlock.
    private readonly Lock _videoGate = new Lock();
    private readonly ManualResetEventSlim _videoSignal = new ManualResetEventSlim(false);

    private int _videoActive;
    private nint* _videoSlots;
    private int _videoSlotCount;
    private long _videoSlotBytes;
    private nint _videoScratch;
    private int _videoFrameBytes;

    // The ring's indices only ever grow; a slot is index % _videoSlotCount. The callback owns the tail,
    // the reader owns the head.
    private long _videoHead;
    private long _videoTail;

    // Written by the callback, read by the reader and by TryGetDroppedFrames.
    private long _videoArrivals;
    private long _videoSequenceGaps;
    private long _videoDiscarded;
    private uint _videoLastSequence;
    private bool _videoHasSequence;
    private int _videoSequenceRun;
    private int _videoSequenceTrusted;
    private int _videoSequenceGarbage;
    private bool _videoJumpPending;
    private uint _videoJumpSequence;
    private uint _videoJumpLost;
    private int _videoSequenceBroken;
    private uint _videoFault;

    // Set by a stop, taken by the next exposure: frames that left the camera after the switch back to
    // trigger mode may still be queued in the SDK, and an exposure must not read one as its own.
    private int _drainBeforeTrigger;

    internal bool IsStreaming => Volatile.Read(ref _videoActive) != 0;

    /// <summary>
    /// Frames lost since the stream started: gaps in the camera's frame sequence (frames that never
    /// reached the callback) plus frames pulled here and thrown away because the ring was full. Null
    /// when this stream's sequence numbers are not a count (see <see cref="NoteSequence"/>), so frames
    /// the camera lost cannot be known.
    /// </summary>
    internal int? DroppedFrames
        => Volatile.Read(ref _videoSequenceBroken) != 0
           || (Volatile.Read(ref _videoSequenceTrusted) == 0 && Volatile.Read(ref _videoArrivals) >= SequenceTrustWithin)
            ? null
            : (int)Math.Min(int.MaxValue, Volatile.Read(ref _videoSequenceGaps) + Volatile.Read(ref _videoDiscarded));

    /// <summary>
    /// Switches the running SDK stream to video with the window, format and controls set now.
    /// </summary>
    internal CMOSErrorCode StartVideo()
    {
        if (IsStreaming)
        {
            return CMOSErrorCode.VideoModeActive;
        }

        if (Volatile.Read(ref _state) is StateExposing)
        {
            return CMOSErrorCode.ExposureInProgress;
        }

        if (!TryGetFinalSize(out var width, out var height))
        {
            return CMOSErrorCode.GeneralError;
        }

        var frameBytes = width * height * (Bits16 ? 2 : 1);
        EnsureVideoRing(frameBytes);

        // Whatever an unread exposure left in the SDK goes now, or the stream's first frame would be it.
        _ = Api.PutOption(Handle, OPTION_FLUSH, FLUSH_SOFT);
        Volatile.Write(ref _state, StateIdle);
        Volatile.Write(ref _drainBeforeTrigger, 0);

        lock (_videoGate)
        {
            _videoFrameBytes = frameBytes;
            _videoHead = 0;
            _videoTail = 0;
            _videoArrivals = 0;
            _videoSequenceGaps = 0;
            _videoDiscarded = 0;
            _videoHasSequence = false;
            _videoSequenceRun = 0;
            _videoSequenceTrusted = 0;
            _videoSequenceGarbage = 0;
            _videoJumpPending = false;
            _videoSequenceBroken = 0;
            _videoFault = 0;
            _videoSignal.Reset();
            Volatile.Write(ref _videoActive, 1);
        }

        var hr = Api.PutOption(Handle, OPTION_TRIGGER, TRIGGER_VIDEO);
        if (!Succeeded(hr))
        {
            lock (_videoGate)
            {
                Volatile.Write(ref _videoActive, 0);
            }

            return ToupcamCamera.ToDALError(hr);
        }

        return CMOSErrorCode.Success;
    }

    /// <summary>
    /// Puts the camera back in software trigger mode, so the next single exposure runs exactly as one
    /// that never followed a stream.
    /// </summary>
    /// <remarks>
    /// <para>A late video frame read as the next exposure's would be a plausible picture with the
    /// wrong exposure time, so the stop is guarded three ways: it waits until no frame has arrived for
    /// twice the exposure plus 50 ms (at most <see cref="MaxVideoQuietMs"/>), still pulling into the
    /// ring meanwhile; it flushes the SDK's own queue; and it marks the next exposure to drain whatever
    /// is left. Measured on the G3M678M, none of them has yet had anything to catch: in 55 traced
    /// stops no frame arrived after the switch, and a 200 ms exposure at the highest gain straight
    /// after a stream read its own frame (the brightest 0.1 percent at 95 ADU, against the stream's
    /// 15).</para>
    /// <para><b>The flush is soft</b> (<see cref="FLUSH_SOFT"/>, the library's queue only): the hard
    /// one, which empties the camera's DDR, is what made streams stall on this body.</para>
    /// <para>On a camera that has already stalled, the switch back blocks about 5 s inside the SDK
    /// before it returns, and still succeeds.</para>
    /// </remarks>
    internal CMOSErrorCode StopVideo()
    {
        if (!IsStreaming)
        {
            return CMOSErrorCode.Success;
        }

        var hr = Api.PutOption(Handle, OPTION_TRIGGER, TRIGGER_SOFTWARE);
        WaitForVideoQuiet();

        lock (_videoGate)
        {
            Volatile.Write(ref _videoActive, 0);
        }

        _ = Api.PutOption(Handle, OPTION_FLUSH, FLUSH_SOFT);
        Volatile.Write(ref _drainBeforeTrigger, 1);
        Volatile.Write(ref _state, StateIdle);
        // A reader still waiting wakes to find the stream gone.
        _videoSignal.Set();
        return ToupcamCamera.ToDALError(hr);
    }

    private void WaitForVideoQuiet()
    {
        uint exposureUs;
        var quietMs = Succeeded(Api.GetExpoTime(Handle, &exposureUs)) ? (int)Math.Min(exposureUs / 500u + 50u, MaxVideoQuietMs) : 50;
        var deadline = Environment.TickCount64 + MaxVideoQuietMs;
        var seen = Volatile.Read(ref _videoArrivals);
        var quietSince = Environment.TickCount64;
        while (Environment.TickCount64 < deadline)
        {
            Thread.Sleep(5);
            var now = Volatile.Read(ref _videoArrivals);
            if (now != seen)
            {
                seen = now;
                quietSince = Environment.TickCount64;
            }
            else if (Environment.TickCount64 - quietSince >= quietMs)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Waits at most <paramref name="waitMs"/> for the stream's next frame and copies it into
    /// <paramref name="buffer"/>.
    /// </summary>
    /// <returns>
    /// <see cref="CMOSErrorCode.Timeout"/> when no frame arrived in the time, which is not a failure.
    /// <see cref="CMOSErrorCode.GeneralError"/> once the SDK has reported the stream STALLED (a no-packet
    /// or no-frame timeout) or a generic error, and the frames that arrived before it have been handed
    /// out: the camera is still on the bus but no longer sending, which no DAL code names, and
    /// <see cref="ToupcamCamera.ResetDevice"/> is what brings it back. Not <see cref="CMOSErrorCode.Timeout"/>,
    /// because a caller asks again on that, for ever. <see cref="CMOSErrorCode.CameraRemoved"/> when the
    /// SDK reported the camera disconnected.
    /// </returns>
    internal CMOSErrorCode GetVideoFrame(nint buffer, int bufferSize, int waitMs)
    {
        if (!IsStreaming)
        {
            return CMOSErrorCode.InvalidSequence;
        }

        if (bufferSize < _videoFrameBytes)
        {
            return CMOSErrorCode.BufferTooSmall;
        }

        var deadline = Environment.TickCount64 + Math.Max(0, waitMs);
        while (true)
        {
            // Reset BEFORE looking, so a frame published after the look sets the event this wait sees.
            _videoSignal.Reset();
            var head = _videoHead;
            if (head != Volatile.Read(ref _videoTail))
            {
                var slot = _videoSlots[head % _videoSlotCount];
                NativeMemory.Copy((void*)slot, (void*)buffer, (nuint)_videoFrameBytes);
                Volatile.Write(ref _videoHead, head + 1);
                return CMOSErrorCode.Success;
            }

            if (Volatile.Read(ref _videoFault) is var fault and not 0)
            {
                return fault is EVENT_DISCONNECTED ? CMOSErrorCode.CameraRemoved : CMOSErrorCode.GeneralError;
            }

            if (!IsStreaming)
            {
                return CMOSErrorCode.InvalidSequence;
            }

            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
            {
                return CMOSErrorCode.Timeout;
            }

            _ = _videoSignal.Wait((int)remaining);
        }
    }

    /// <summary>On the SDK's callback thread: one frame is ready, so pull it into the ring.</summary>
    /// <returns>False when no stream is running, so the frame is the trigger path's.</returns>
    private bool OnVideoFrame()
    {
        lock (_videoGate)
        {
            if (Volatile.Read(ref _videoActive) == 0)
            {
                return false;
            }

            var tail = _videoTail;
            var full = tail - Volatile.Read(ref _videoHead) >= _videoSlotCount;
            var target = full ? _videoScratch : _videoSlots[tail % _videoSlotCount];
            // pInfo is not optional in every build of the SDK; ToupcamFrameInfoV4 is ~104 bytes.
            var info = stackalloc byte[512];
            if (!Succeeded(Api.PullImageV4(Handle, (void*)target, 0, 0, 0, info)))
            {
                return true;
            }

            Volatile.Write(ref _videoArrivals, _videoArrivals + 1);
            NoteSequence(info);
            if (full)
            {
                Volatile.Write(ref _videoDiscarded, _videoDiscarded + 1);
                return true;
            }

            Volatile.Write(ref _videoTail, tail + 1);
        }

        _videoSignal.Set();
        return true;
    }

    /// <summary>
    /// Counts the frames missing between this one and the last, by the camera's own sequence number,
    /// when the SDK reports one.
    /// </summary>
    /// <remarks>
    /// <para><b>The number is only believed while it behaves as a count</b>, because on the G3M678M it
    /// does not always. On the first handle opened after a <c>TOUPCAM_OPTION_DEVICE_RESET</c> every
    /// frame carries <c>TOUPCAM_FRAMEINFO_FLAG_SEQ</c> but the sequence (and the timestamp) cycles
    /// through five fixed values (0, 16777984, 16777216, 65537, 65792), while the pixels and the rate
    /// are fine; read as gaps, that was 51 frames "lost" for every one delivered (<see cref="Acquire"/>
    /// now opens twice after a reset, which cures it). And on a good handle a frame now and then
    /// carries such a value in the middle of a run that otherwise counts by one (three in 3,700 frames
    /// in one stream, none in 12,000 in others), the next good number sometimes skipping one more
    /// than the frames between.</para>
    /// <para>So: a stream's sequence is trusted once it has stepped by exactly one
    /// <see cref="SequenceTrustSteps"/> times in a row, and only a trusted one counts gaps. A jump
    /// forward is counted only once the next frame continues from it, since a bad value can land a
    /// plausible distance ahead. A frame whose number does not fit (backwards, the same, too far ahead,
    /// or a jump the next frame does not confirm) is taken as one frame with bad info, and the next good
    /// number is measured from the last good one with that frame allowed for.
    /// <see cref="SequenceGarbageLimit"/> such frames in a row, or no trust within
    /// <see cref="SequenceTrustWithin"/> frames, and the camera-side count is unknown, which
    /// <see cref="DroppedFrames"/> then says rather than saying zero.</para>
    /// </remarks>
    private void NoteSequence(byte* info)
    {
        if ((*(uint*)(info + FRAMEINFO_FLAG_OFFSET) & FRAMEINFO_FLAG_SEQ) == 0)
        {
            return;
        }

        var sequence = *(uint*)(info + FRAMEINFO_SEQ_OFFSET);
        if (!_videoHasSequence)
        {
            _videoLastSequence = sequence;
            _videoHasSequence = true;
            return;
        }

        if (_videoSequenceRun < SequenceTrustSteps)
        {
            // Not trusted yet: only a step of one builds trust, and anything else starts again here.
            _videoSequenceRun = unchecked(sequence - _videoLastSequence) == 1 ? _videoSequenceRun + 1 : 0;
            _videoLastSequence = sequence;
            if (_videoSequenceRun == SequenceTrustSteps)
            {
                Volatile.Write(ref _videoSequenceTrusted, 1);
            }

            return;
        }

        if (_videoJumpPending)
        {
            _videoJumpPending = false;
            if (sequence == unchecked(_videoJumpSequence + 1))
            {
                Volatile.Write(ref _videoSequenceGaps, _videoSequenceGaps + _videoJumpLost);
                _videoLastSequence = sequence;
                _videoSequenceGarbage = 0;
                return;
            }

            // The jump was not confirmed: that frame carried bad info.
            _videoSequenceGarbage++;
        }

        // Frames with bad info since the last good number were frames too, so each moves the count on.
        var step = unchecked(sequence - _videoLastSequence);
        var expected = 1u + (uint)_videoSequenceGarbage;
        if (step == expected)
        {
            _videoLastSequence = sequence;
            _videoSequenceGarbage = 0;
        }
        else if (step > expected && step <= MaxSequenceGap)
        {
            (_videoJumpPending, _videoJumpSequence, _videoJumpLost) = (true, sequence, step - expected);
        }
        else if (++_videoSequenceGarbage >= SequenceGarbageLimit)
        {
            Volatile.Write(ref _videoSequenceBroken, 1);
        }
    }

    /// <summary>On the SDK's callback thread: the stream failed. True when a stream was running to hear it.</summary>
    private bool OnVideoFault(uint nEvent)
    {
        if (!IsStreaming)
        {
            return false;
        }

        _ = Interlocked.CompareExchange(ref _videoFault, nEvent, 0);
        _videoSignal.Set();
        return true;
    }

    /// <summary>Sizes the ring for frames of <paramref name="frameBytes"/>, reusing it when it is big
    /// enough. Only called with no stream running, so no pull can be writing into it.</summary>
    private void EnsureVideoRing(int frameBytes)
    {
        var slots = (int)Math.Clamp(VideoRingBudgetBytes / Math.Max(1, frameBytes), MinVideoSlots, MaxVideoSlots);
        if (_videoSlots != null && frameBytes <= _videoSlotBytes && slots <= _videoSlotCount)
        {
            return;
        }

        FreeVideoRing();
        _videoSlots = (nint*)NativeMemory.AllocZeroed((nuint)slots, (nuint)sizeof(nint));
        for (var i = 0; i < slots; i++)
        {
            _videoSlots[i] = (nint)NativeMemory.Alloc((nuint)frameBytes);
        }

        _videoScratch = (nint)NativeMemory.Alloc((nuint)frameBytes);
        _videoSlotCount = slots;
        _videoSlotBytes = frameBytes;
    }

    /// <summary>Frees the ring. Only after <c>Toupcam_Stop</c>, or with no stream running.</summary>
    private void FreeVideoRing()
    {
        if (_videoSlots == null)
        {
            return;
        }

        for (var i = 0; i < _videoSlotCount; i++)
        {
            NativeMemory.Free((void*)_videoSlots[i]);
        }

        NativeMemory.Free(_videoSlots);
        NativeMemory.Free((void*)_videoScratch);
        _videoSlots = null;
        _videoScratch = 0;
        _videoSlotCount = 0;
        _videoSlotBytes = 0;
    }

    // ---- Frame rate ----------------------------------------------------------------------------------

    /// <summary>
    /// The frame rate the SDK measured over its last few seconds (<c>Toupcam_get_FrameRate</c>), or
    /// null when it has none.
    /// </summary>
    internal double? FrameRate()
    {
        uint frames, milliseconds, total;
        return Succeeded(Api.GetFrameRate(Handle, &frames, &milliseconds, &total)) && milliseconds > 0
            ? frames * 1000d / milliseconds
            : null;
    }
}
