using System.Text;
using LaptopTester.Core.Services;
using Xunit;

namespace LaptopTester.Tests.CoreLayer;

public sealed class AudioSynthesisTests
{
    [Fact]
    public void BuildStereoWave_440Hz1Sec_ProducesValidPcmWavHeaderAndSamples()
    {
        byte[] wav = AudioCameraService.BuildStereoWave(left: true, right: true, 440, 440, 1.0);
        int expectedDataBytes = 44100 * 4;

        Assert.Equal(44 + expectedDataBytes, wav.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(36 + expectedDataBytes, BitConverter.ToInt32(wav, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal(16, BitConverter.ToInt32(wav, 16));
        Assert.Equal(1, BitConverter.ToInt16(wav, 20)); // PCM
        Assert.Equal(2, BitConverter.ToInt16(wav, 22)); // stereo
        Assert.Equal(44100, BitConverter.ToInt32(wav, 24));
        Assert.Equal(176400, BitConverter.ToInt32(wav, 28));
        Assert.Equal(4, BitConverter.ToInt16(wav, 32));
        Assert.Equal(16, BitConverter.ToInt16(wav, 34));
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(expectedDataBytes, BitConverter.ToInt32(wav, 40));

        // First frame has 0 envelope (fade-in)
        Assert.Equal(0, BitConverter.ToInt16(wav, 44));
        Assert.Equal(0, BitConverter.ToInt16(wav, 46));

        short maxAbs = 0;
        for (int off = 44; off < wav.Length; off += 4)
        {
            short l = BitConverter.ToInt16(wav, off);
            short r = BitConverter.ToInt16(wav, off + 2);
            Assert.Equal(l, r);
            maxAbs = Math.Max(maxAbs, Math.Abs(l));
        }
        Assert.InRange(maxAbs, 1, 14000);
    }

    [Fact]
    public void BuildStereoWave_ChannelIsolation_ZeroesInactiveChannel()
    {
        byte[] leftOnly = AudioCameraService.BuildStereoWave(left: true, right: false, 440, 440, 0.25);
        bool anyLeftNonZero = false;
        for (int off = 44; off < leftOnly.Length; off += 4)
        {
            short l = BitConverter.ToInt16(leftOnly, off);
            short r = BitConverter.ToInt16(leftOnly, off + 2);
            if (l != 0) anyLeftNonZero = true;
            Assert.Equal(0, r);
        }
        Assert.True(anyLeftNonZero);

        byte[] rightOnly = AudioCameraService.BuildStereoWave(left: false, right: true, 440, 440, 0.25);
        bool anyRightNonZero = false;
        for (int off = 44; off < rightOnly.Length; off += 4)
        {
            short l = BitConverter.ToInt16(rightOnly, off);
            short r = BitConverter.ToInt16(rightOnly, off + 2);
            Assert.Equal(0, l);
            if (r != 0) anyRightNonZero = true;
        }
        Assert.True(anyRightNonZero);
    }

    [Fact]
    public void BuildStereoWave_FrequencySweep_IncreasesZeroCrossingRate()
    {
        byte[] sweep = AudioCameraService.BuildStereoWave(left: true, right: true, 160, 4000, 1.0);
        int totalFrames = (sweep.Length - 44) / 4;
        int windowFrames = totalFrames / 10;

        int CountZeroCrossings(int startFrame, int count)
        {
            int crossings = 0;
            short prev = BitConverter.ToInt16(sweep, 44 + startFrame * 4);
            for (int f = 1; f < count; f++)
            {
                short cur = BitConverter.ToInt16(sweep, 44 + (startFrame + f) * 4);
                if ((prev < 0 && cur >= 0) || (prev >= 0 && cur < 0))
                    crossings++;
                prev = cur;
            }
            return crossings;
        }

        int earlyCrossings = CountZeroCrossings(0, windowFrames);
        int lateCrossings = CountZeroCrossings(totalFrames - windowFrames, windowFrames);
        Assert.True(lateCrossings > earlyCrossings * 5,
            $"Expected sweep end crossings ({lateCrossings}) > 5x start ({earlyCrossings}).");
    }

    [Fact]
    public void WrapMonoPcmAsWav_WritesMonoHeaderAndPreservesPayload()
    {
        byte[] pcm = [1, 2, 3, 4, 5, 6];
        byte[] wav = AudioCameraService.WrapMonoPcmAsWav(pcm, 16000);

        Assert.Equal(44 + pcm.Length, wav.Length);
        Assert.Equal(1, BitConverter.ToInt16(wav, 22));
        Assert.Equal(16000, BitConverter.ToInt32(wav, 24));
        Assert.Equal(32000, BitConverter.ToInt32(wav, 28));
        Assert.Equal(pcm, wav[44..]);
    }

    [Fact]
    public void ComputeNormalizedRms_HandlesSilenceFullScaleConstantAndEmpty()
    {
        Assert.Equal(0.0, AudioCameraService.ComputeNormalizedRms([]));
        Assert.Equal(0.0, AudioCameraService.ComputeNormalizedRms(new byte[200]));

        byte[] fullScale = new byte[200];
        for (int i = 0; i < 100; i++)
            BitConverter.GetBytes((short)(i % 2 == 0 ? 32767 : -32768)).CopyTo(fullScale, i * 2);
        Assert.Equal(1.0, AudioCameraService.ComputeNormalizedRms(fullScale), 6);

        byte[] amp1000 = new byte[200];
        for (int i = 0; i < 100; i++)
            BitConverter.GetBytes((short)1000).CopyTo(amp1000, i * 2);
        double expected = (1000.0 / 32768.0) * 3.2;
        Assert.Equal(expected, AudioCameraService.ComputeNormalizedRms(amp1000), 3);
    }
}
