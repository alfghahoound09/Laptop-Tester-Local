using System.Windows.Threading;
using LaptopTester.Core.Services;
using Xunit;

namespace LaptopTester.Tests.CoreLayer;

[Collection("Audio")]
public sealed class AudioCameraLiveTests
{
    [Fact]
    [Trait("Category", "Hardware")]
    public async Task GetAudioDevices_And_EnumerateCameras_DoNotThrow()
    {
        using var svc = new AudioCameraService();
        var devs = svc.GetAudioDevices();

        Assert.True(devs.OutputDeviceCount >= 0);
        Assert.True(devs.InputDeviceCount >= 0);
        Assert.False(string.IsNullOrWhiteSpace(devs.PrimaryOutputName));
        Assert.False(string.IsNullOrWhiteSpace(devs.PrimaryInputName));

        var cams = await svc.EnumerateCamerasAsync();
        Assert.All(cams, c => Assert.False(string.IsNullOrWhiteSpace(c)));
    }

    [SkippableFact]
    [Trait("Category", "Hardware")]
    public async Task MicrophoneRecording_CapturesPcmAndStopsIdempotently()
    {
        using var svc = new AudioCameraService();
        var devs = svc.GetAudioDevices();
        Skip.If(devs.InputDeviceCount == 0, "No audio input (microphone) device present.");

        bool started = svc.StartMicrophoneRecording();
        Skip.If(!started, "Microphone capture could not open WAVE_MAPPER (hardware/permission restricted).");

        await Task.Delay(600);
        svc.StopMicrophoneRecording();
        svc.StopMicrophoneRecording(); // idempotent

        Assert.True(svc.HasRecordedAudio);
    }

    [SkippableFact]
    [Trait("Category", "Audible")]
    public async Task SpeakerPlayback_PlaysStereoSweepAndHandlesEmptyRecordedBuffer()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("LAPTOPTESTER_AUDIBLE") == "1",
            "Set LAPTOPTESTER_AUDIBLE=1 to run audible speaker playback tests.");

        using var svc = new AudioCameraService();
        svc.PlayStereoTest(true, false, 440, 0.2);
        await Task.Delay(120);
        svc.PlayStereoTest(false, true, 523, 0.2);
        await Task.Delay(120);
        svc.PlayStereoTest(true, true, 440, 0.2);
        await Task.Delay(120);
        svc.PlaySweepTest(0.3);
        await Task.Delay(150);

        svc.PlayRecordedMicrophone(); // empty buffer no-op
        svc.StopPlayback();
        svc.StopPlayback(); // idempotent
    }

    [SkippableFact]
    [Trait("Category", "Hardware")]
    public async Task AcousticLoopback_WithWpfDispatcherAndEventSubscriber_DoesNotDeadlock()
    {
        using var svc = new AudioCameraService();
        var devs = svc.GetAudioDevices();
        Skip.If(devs.InputDeviceCount == 0 || devs.OutputDeviceCount == 0,
            "Requires both audio output and input devices.");

        var tcs = new TaskCompletionSource<AcousticLoopbackResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int uiCallbacks = 0;

        var staThread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));

            svc.MicrophoneLevelMeasured += (_, _) =>
            {
                if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
                {
                    dispatcher.BeginInvoke(DispatcherPriority.Background, () => Interlocked.Increment(ref uiCallbacks));
                }
            };

            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    // Runs loopback from a real WPF UI Dispatcher context while async UI callbacks fire.
                    var result = await svc.RunAcousticLoopbackTestAsync(chimeDurationSec: 0.25);
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }));

            Dispatcher.Run();
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.IsBackground = true;
        staThread.Start();

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(completed == tcs.Task, "Audio loopback deadlocked the WPF Dispatcher thread.");

        var loopback = await tcs.Task;
        Assert.True(loopback.OutputPresent);
        Assert.False(string.IsNullOrWhiteSpace(loopback.Summary));
        Assert.True(staThread.Join(2000), "WPF STA Dispatcher thread did not exit cleanly.");
    }
}
