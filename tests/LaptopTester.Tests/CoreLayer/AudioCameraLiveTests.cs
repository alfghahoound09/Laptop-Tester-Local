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
}
