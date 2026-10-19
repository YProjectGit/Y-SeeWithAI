using System.Threading;
using UnityEngine;

/// <summary>1つのAudioSourceを鳴らし続け、音声スレッドからPCMを途切れずに供給する。</summary>
[RequireComponent(typeof(AudioSource))]
public sealed class PcmStreamPlayer : MonoBehaviour
{
    PcmStreamBuffer buffer;
    AudioSource source;
    AudioClip carrier;
    int outputRate;
    double deviceLatency;
    double drainUntil;
    readonly object waveformGate = new object();
    readonly float[] waveform = new float[1024];

    public void GetOutputData(float[] samples)
    {
        lock (waveformGate)
        {
            System.Array.Clear(samples, 0, samples.Length);
            System.Array.Copy(waveform, samples, System.Math.Min(waveform.Length, samples.Length));
        }
    }

    public bool HasPendingAudio => buffer != null &&
        (buffer.PendingSamples > 0 || AudioSettings.dspTime < Volatile.Read(ref drainUntil));
    public int UnderrunCount => buffer == null ? 0 : buffer.UnderrunCount;
    public long RenderedFrames => buffer == null ? 0 : buffer.RenderedFrames;

    public void Initialize(int inputRate, float prebufferSeconds)
    {
        if (buffer != null) return;
        source = GetComponent<AudioSource>();
        outputRate = AudioSettings.outputSampleRate;
        AudioSettings.GetDSPBufferSize(out int length, out int buffers);
        deviceLatency = (double)(length * buffers) / outputRate;
        buffer = new PcmStreamBuffer(inputRate, prebufferSeconds);
        carrier = AudioClip.Create("Continuous PCM", outputRate, 1, outputRate, false);
        source.clip = carrier;
        source.loop = true;
        source.spatialBlend = 0f;
        source.pitch = 1f;
        source.Play();
    }
    public void Enqueue(byte[] pcm) { buffer?.Enqueue(pcm); }
    public void Complete() { buffer?.Complete(); }
    public void Clear()
    {
        buffer?.Clear();
        Volatile.Write(ref drainUntil, 0);
    }
    void OnAudioFilterRead(float[] data, int channels)
    {
        if (buffer == null) return;
        if (buffer.Render(data, channels, outputRate) > 0)
            Volatile.Write(ref drainUntil, AudioSettings.dspTime + (double)(data.Length / channels) / outputRate + deviceLatency);
        // AudioSource.GetOutputDataではフィルター生成前の無音が返るため、実際に出力した波形を共有する。
        lock (waveformGate)
        {
            int frames = System.Math.Min(waveform.Length, data.Length / channels);
            for (int i = 0; i < frames; i++) waveform[i] = data[i * channels];
            System.Array.Clear(waveform, frames, waveform.Length - frames);
        }
    }
    void OnDestroy()
    {
        if (source != null) { source.Stop(); source.loop = false; source.clip = null; }
        if (carrier != null) Destroy(carrier);
    }
}
