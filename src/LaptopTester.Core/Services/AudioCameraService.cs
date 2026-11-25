using System.IO;
using System.Runtime.InteropServices;

namespace LaptopTester.Core.Services;

public sealed record AudioDeviceSummary(int OutputDeviceCount, string PrimaryOutputName, int InputDeviceCount, string PrimaryInputName);

public sealed record AcousticLoopbackResult(
    bool OutputPresent,
    bool InputPresent,
    double PeakNormalizedRms,
    bool LoopbackDetected,
    string Summary);

/// <summary>Synthesizes stereo test WAVs, records live microphone PCM via winmm.dll (CALLBACK_EVENT), and enumerates cameras.</summary>
public sealed class AudioCameraService : IDisposable
{
    private const int CALLBACK_EVENT = 0x00050000;
    private const uint WHDR_DONE = 0x00000001;
    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_MEMORY = 0x0004;
    private const uint SND_PURGE = 0x0040;

    private readonly object _playbackLock = new();
    private readonly object _waveInLifecycleLock = new();
    private readonly object _recLock = new();
    private readonly MemoryStream _recordedPcm = new();

    private GCHandle? _playbackHandle;
    private IntPtr _waveInHandle;
    private readonly List<GCHandle> _bufHandles = new();
    private readonly List<IntPtr> _headerPtrs = new();
    private AutoResetEvent? _dataReadyEvent;
    private ManualResetEvent? _stopEvent;
    private Thread? _readerThread;
    private volatile bool _isRecording;

    public event EventHandler<double>? MicrophoneLevelMeasured;

    [DllImport("winmm.dll")]
    private static extern bool PlaySound(IntPtr pszSound, IntPtr hmod, uint fdwSound);

    [DllImport("winmm.dll")]
    private static extern int waveOutGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int waveOutGetDevCaps(IntPtr uDeviceID, out WAVEOUTCAPS pwoc, int cbwoc);

    [DllImport("winmm.dll")]
    private static extern int waveInGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int waveInGetDevCaps(IntPtr uDeviceID, out WAVEINCAPS pwic, int cbwic);

    [DllImport("winmm.dll")]
    private static extern int waveInOpen(out IntPtr lphWaveIn, int uDeviceID, ref WAVEFORMATEX lpFormat, IntPtr dwCallback, IntPtr dwInstance, int dwFlags);

    [DllImport("winmm.dll")]
    private static extern int waveInPrepareHeader(IntPtr hWaveIn, IntPtr lpWaveInHdr, int uSize);

    [DllImport("winmm.dll")]
    private static extern int waveInUnprepareHeader(IntPtr hWaveIn, IntPtr lpWaveInHdr, int uSize);

    [DllImport("winmm.dll")]
    private static extern int waveInAddBuffer(IntPtr hWaveIn, IntPtr lpWaveInHdr, int uSize);

    [DllImport("winmm.dll")]
    private static extern int waveInStart(IntPtr hWaveIn);

    [DllImport("winmm.dll")]
    private static extern int waveInStop(IntPtr hWaveIn);

    [DllImport("winmm.dll")]
    private static extern int waveInReset(IntPtr hWaveIn);

    [DllImport("winmm.dll")]
    private static extern int waveInClose(IntPtr hWaveIn);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WAVEOUTCAPS
    {
        public ushort wMid, wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
        public uint dwFormats;
        public ushort wChannels, wReserved1;
        public uint dwSupport;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WAVEINCAPS
    {
        public ushort wMid, wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
        public uint dwFormats;
        public ushort wChannels, wReserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag, nChannels;
        public uint nSamplesPerSec, nAvgBytesPerSec;
        public ushort nBlockAlign, wBitsPerSample, cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength, dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags, dwLoops;
        public IntPtr lpNext, reserved;
    }

    public AudioDeviceSummary GetAudioDevices()
    {
        int outCount = waveOutGetNumDevs();
        string outName = "None detected";
        if (outCount > 0 && waveOutGetDevCaps(IntPtr.Zero, out var woc, Marshal.SizeOf<WAVEOUTCAPS>()) == 0)
            outName = woc.szPname;

        int inCount = waveInGetNumDevs();
        string inName = "None detected";
        if (inCount > 0 && waveInGetDevCaps(IntPtr.Zero, out var wic, Marshal.SizeOf<WAVEINCAPS>()) == 0)
            inName = wic.szPname;

        return new AudioDeviceSummary(outCount, outName, inCount, inName);
    }

    public void PlayStereoTest(bool left, bool right, double freqHz = 440, double durationSec = 1.4)
    {
        byte[] wav = BuildStereoWave(left, right, freqHz, freqHz, durationSec);
        PlayWaveBytes(wav);
    }

    public void PlaySweepTest(double durationSec = 2.5)
    {
        byte[] wav = BuildStereoWave(true, true, 160, 4000, durationSec);
        PlayWaveBytes(wav);
    }

    private void PlayWaveBytes(byte[] wav)
    {
        lock (_playbackLock)
        {
            StopPlaybackCore();
            _playbackHandle = GCHandle.Alloc(wav, GCHandleType.Pinned);
            PlaySound(_playbackHandle.Value.AddrOfPinnedObject(), IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
        }
    }

    public void StopPlayback()
    {
        lock (_playbackLock)
        {
            StopPlaybackCore();
        }
    }

    private void StopPlaybackCore()
    {
        try { PlaySound(IntPtr.Zero, IntPtr.Zero, SND_PURGE); } catch { }
        if (_playbackHandle is { IsAllocated: true } h)
            h.Free();
        _playbackHandle = null;
    }

    public bool StartMicrophoneRecording()
    {
        lock (_waveInLifecycleLock)
        {
            StopMicrophoneRecordingCore();
            lock (_recLock) _recordedPcm.SetLength(0);

            var fmt = new WAVEFORMATEX
            {
                wFormatTag = 1, // PCM
                nChannels = 1,
                nSamplesPerSec = 16000,
                wBitsPerSample = 16,
                nBlockAlign = 2,
                nAvgBytesPerSec = 32000,
                cbSize = 0
            };

            var dataEvent = new AutoResetEvent(false);
            var stopEvent = new ManualResetEvent(false);

            int rc = waveInOpen(
                out IntPtr handle,
                -1 /* WAVE_MAPPER */,
                ref fmt,
                dataEvent.SafeWaitHandle.DangerousGetHandle(),
                IntPtr.Zero,
                CALLBACK_EVENT);

            if (rc != 0 || handle == IntPtr.Zero)
            {
                dataEvent.Dispose();
                stopEvent.Dispose();
                return false;
            }

            _waveInHandle = handle;
            _dataReadyEvent = dataEvent;
            _stopEvent = stopEvent;
            _isRecording = true;

            int hdrSize = Marshal.SizeOf<WAVEHDR>();
            var sessionHeaders = new List<IntPtr>(4);
            try
            {
                for (int i = 0; i < 4; i++)
                {
                    byte[] buf = new byte[1600]; // 50ms chunks at 16kHz 16-bit mono
                    var bufHandle = GCHandle.Alloc(buf, GCHandleType.Pinned);
                    var hdr = new WAVEHDR { lpData = bufHandle.AddrOfPinnedObject(), dwBufferLength = (uint)buf.Length };
                    IntPtr hdrPtr = Marshal.AllocHGlobal(hdrSize);
                    Marshal.StructureToPtr(hdr, hdrPtr, false);
                    _bufHandles.Add(bufHandle);
                    _headerPtrs.Add(hdrPtr);
                    sessionHeaders.Add(hdrPtr);
                    waveInPrepareHeader(handle, hdrPtr, hdrSize);
                    waveInAddBuffer(handle, hdrPtr, hdrSize);
                }

                int startRc = waveInStart(handle);
                if (startRc != 0)
                {
                    StopMicrophoneRecordingCore();
                    return false;
                }
            }
            catch
            {
                StopMicrophoneRecordingCore();
                return false;
            }

            // Dedicated event-driven reader thread avoids re-entering winmm inside waveInProc
            // and never holds WinMM's internal device lock while notifying UI subscribers.
            _readerThread = new Thread(() => WaveInReaderLoop(handle, sessionHeaders, dataEvent, stopEvent, hdrSize))
            {
                IsBackground = true,
                Name = "LaptopTester-WaveInReader"
            };
            _readerThread.Start();
            return true;
        }
    }

    private void WaveInReaderLoop(
        IntPtr handle,
        IReadOnlyList<IntPtr> headers,
        AutoResetEvent dataEvent,
        ManualResetEvent stopEvent,
        int hdrSize)
    {
        WaitHandle[] waits = [dataEvent, stopEvent];
        var pendingLevels = new List<double>(4);

        while (_isRecording)
        {
            int signaled;
            try
            {
                signaled = WaitHandle.WaitAny(waits, 200);
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (signaled == 1 || !_isRecording)
                break;

            pendingLevels.Clear();
            foreach (var hdrPtr in headers)
            {
                if (!_isRecording)
                    break;

                var hdr = Marshal.PtrToStructure<WAVEHDR>(hdrPtr);
                if ((hdr.dwFlags & WHDR_DONE) == 0)
                    continue;

                if (hdr.dwBytesRecorded >= 2)
                {
                    byte[] chunk = new byte[hdr.dwBytesRecorded];
                    Marshal.Copy(hdr.lpData, chunk, 0, chunk.Length);
                    lock (_recLock) _recordedPcm.Write(chunk, 0, chunk.Length);
                    pendingLevels.Add(ComputeNormalizedRms(chunk));
                }

                if (_isRecording)
                {
                    try { waveInAddBuffer(handle, hdrPtr, hdrSize); } catch { }
                }
            }

            foreach (double level in pendingLevels)
            {
                if (!_isRecording)
                    break;
                try
                {
                    MicrophoneLevelMeasured?.Invoke(this, level);
                }
                catch
                {
                    // Never allow a subscriber exception to crash or stall audio capture.
                }
            }
        }
    }

    public bool HasRecordedAudio
    {
        get { lock (_recLock) return _recordedPcm.Length > 0; }
    }

    public void PlayRecordedMicrophone()
    {
        byte[] pcm;
        lock (_recLock) pcm = _recordedPcm.ToArray();
        if (pcm.Length == 0) return;

        byte[] wav = WrapMonoPcmAsWav(pcm, 16000);
        PlayWaveBytes(wav);
    }

    public void StopMicrophoneRecording()
    {
        lock (_waveInLifecycleLock)
        {
            StopMicrophoneRecordingCore();
        }
    }

    private void StopMicrophoneRecordingCore()
    {
        _isRecording = false;
        try { _stopEvent?.Set(); } catch { }
        try { _dataReadyEvent?.Set(); } catch { }

        IntPtr handle = _waveInHandle;
        if (handle != IntPtr.Zero)
        {
            try { waveInStop(handle); } catch { }
            try { waveInReset(handle); } catch { }
        }

        // Wait for the background reader loop to exit before unpreparing/freeing buffers.
        var thread = _readerThread;
        _readerThread = null;
        bool readerExited = true;
        if (thread is { IsAlive: true } && thread != Thread.CurrentThread)
        {
            readerExited = thread.Join(1500);
        }

        if (handle != IntPtr.Zero)
        {
            int hdrSize = Marshal.SizeOf<WAVEHDR>();
            foreach (var ptr in _headerPtrs)
            {
                try { waveInUnprepareHeader(handle, ptr, hdrSize); } catch { }
            }
            try { waveInClose(handle); } catch { }
            _waveInHandle = IntPtr.Zero;
        }

        if (readerExited)
        {
            foreach (var ptr in _headerPtrs) Marshal.FreeHGlobal(ptr);
            _headerPtrs.Clear();
            foreach (var h in _bufHandles)
            {
                if (h.IsAllocated) h.Free();
            }
            _bufHandles.Clear();

            try { _dataReadyEvent?.Dispose(); } catch { }
            try { _stopEvent?.Dispose(); } catch { }
            _dataReadyEvent = null;
            _stopEvent = null;
        }
    }

    public async Task<IReadOnlyList<string>> EnumerateCamerasAsync(CancellationToken ct = default)
    {
        var rows = await WmiQueryHelper.QueryAsync(
            "SELECT Name, Status FROM Win32_PnPEntity WHERE PNPClass = 'Camera' OR PNPClass = 'Image'",
            ct: ct).ConfigureAwait(false);
        return rows.Select(r => $"{WmiQueryHelper.Str(r, "Name")} ({WmiQueryHelper.Str(r, "Status", "OK")})")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .ToList();
    }

    /// <summary>Plays a brief tone while listening on the default microphone entirely off the UI thread.</summary>
    public Task<AcousticLoopbackResult> RunAcousticLoopbackTestAsync(
        double chimeDurationSec = 0.75,
        double thresholdNormalizedRms = 0.08,
        CancellationToken ct = default)
    {
        return Task.Run(async () =>
        {
            var summary = GetAudioDevices();
            bool outOk = summary.OutputDeviceCount > 0;
            bool inOk = summary.InputDeviceCount > 0;
            if (!outOk || !inOk)
            {
                return new AcousticLoopbackResult(
                    outOk,
                    inOk,
                    0,
                    false,
                    !outOk && !inOk
                        ? "No speakers or microphone devices detected"
                        : !outOk
                            ? "No speaker output device detected"
                            : "No microphone input device detected");
            }

            double peakRms = 0;
            object peakLock = new();
            bool micStarted = false;

            void Handler(object? _, double level)
            {
                lock (peakLock)
                {
                    if (level > peakRms) peakRms = level;
                }
            }

            MicrophoneLevelMeasured += Handler;
            try
            {
                micStarted = StartMicrophoneRecording();
                if (!micStarted)
                {
                    return new AcousticLoopbackResult(true, false, 0, false,
                        "Microphone capture could not open (in use or permission blocked)");
                }

                await Task.Delay(120, ct).ConfigureAwait(false);
                PlayStereoTest(left: true, right: true, freqHz: 880, durationSec: chimeDurationSec);
                await Task.Delay((int)(chimeDurationSec * 1000) + 150, ct).ConfigureAwait(false);

                double observedPeak;
                lock (peakLock) observedPeak = peakRms;

                bool detected = observedPeak >= thresholdNormalizedRms;
                string note = detected
                    ? $"Acoustic loopback verified speaker + mic (peak {Math.Round(observedPeak * 100):F0}%)"
                    : $"Tone played but mic heard low/no signal ({Math.Round(observedPeak * 100):F0}%) — confirm manually if muted/headphones";
                return new AcousticLoopbackResult(true, true, Math.Round(observedPeak, 3), detected, note);
            }
            finally
            {
                MicrophoneLevelMeasured -= Handler;
                StopPlayback();
                if (micStarted) StopMicrophoneRecording();
            }
        }, ct);
    }

    internal static double ComputeNormalizedRms(byte[] pcm16)
    {
        int samples = pcm16.Length / 2;
        if (samples <= 0) return 0;
        double sumSq = 0;
        for (int i = 0; i < samples; i++)
        {
            short s = BitConverter.ToInt16(pcm16, i * 2);
            double norm = s / 32768.0;
            sumSq += norm * norm;
        }
        double rms = Math.Sqrt(sumSq / samples);
        return Math.Min(1.0, rms * 3.2);
    }

    internal static byte[] BuildStereoWave(bool left, bool right, double startHz, double endHz, double durationSec)
    {
        const int sampleRate = 44100;
        int totalSamples = (int)(sampleRate * durationSec);
        int dataBytes = totalSamples * 4;
        using var ms = new MemoryStream(44 + dataBytes);
        using var bw = new BinaryWriter(ms);

        WriteWavHeader(bw, sampleRate, 2, 16, dataBytes);

        double phase = 0;
        int fadeSamples = (int)(sampleRate * 0.03);
        for (int i = 0; i < totalSamples; i++)
        {
            double t = i / (double)totalSamples;
            double freq = startHz + (endHz - startHz) * t;
            phase += 2.0 * Math.PI * freq / sampleRate;
            double env = 1.0;
            if (i < fadeSamples) env = i / (double)fadeSamples;
            else if (i > totalSamples - fadeSamples) env = (totalSamples - i) / (double)fadeSamples;

            short sample = (short)(Math.Sin(phase) * env * 14000);
            bw.Write(left ? sample : (short)0);
            bw.Write(right ? sample : (short)0);
        }
        return ms.ToArray();
    }

    internal static byte[] WrapMonoPcmAsWav(byte[] pcm, int sampleRate)
    {
        using var ms = new MemoryStream(44 + pcm.Length);
        using var bw = new BinaryWriter(ms);
        WriteWavHeader(bw, sampleRate, 1, 16, pcm.Length);
        bw.Write(pcm);
        return ms.ToArray();
    }

    internal static void WriteWavHeader(BinaryWriter bw, int sampleRate, short channels, short bitsPerSample, int dataBytes)
    {
        bw.Write("RIFF"u8);
        bw.Write(36 + dataBytes);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1);
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(sampleRate * channels * (bitsPerSample / 8));
        bw.Write((short)(channels * (bitsPerSample / 8)));
        bw.Write(bitsPerSample);
        bw.Write("data"u8);
        bw.Write(dataBytes);
    }

    public void Dispose()
    {
        StopPlayback();
        StopMicrophoneRecording();
    }
}
