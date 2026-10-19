using System;

/// <summary>PCM16 mono をチャンク境界によらず連続して読む。Unity API を使わない音声バッファ。</summary>
public sealed class PcmStreamBuffer
{
    readonly object gate = new object();
    readonly int inputRate;
    readonly int prebufferSamples;
    float[] ring;
    int head, count;
    double phase;
    bool completed, running;
    int lowByte = -1;
    float gain, lastOutput;
    int underruns;
    long renderedFrames;

    public PcmStreamBuffer(int sampleRate, float bufferSeconds = 0.15f)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        inputRate = sampleRate;
        prebufferSamples = Math.Max(2, (int)(sampleRate * bufferSeconds));
        ring = new float[Math.Max(sampleRate * 4, prebufferSamples * 2)];
    }
    public int PendingSamples { get { lock (gate) return count; } }
    public int UnderrunCount { get { lock (gate) return underruns; } }
    public long RenderedFrames { get { lock (gate) return renderedFrames; } }

    // 呼び出し元は受信スレッドでもメインスレッドでもよい。
    public void Enqueue(byte[] pcm)
    {
        if (pcm == null || pcm.Length == 0) return;
        lock (gate)
        {
            int needed = count + (pcm.Length + (lowByte >= 0 ? 1 : 0)) / 2;
            if (needed > ring.Length)
            {
                var larger = new float[Math.Max(needed, ring.Length * 2)];
                for (int i = 0; i < count; i++) larger[i] = ring[(head + i) % ring.Length];
                ring = larger; head = 0;
            }
            completed = false;
            for (int i = 0; i < pcm.Length; i++)
            {
                if (lowByte < 0) { lowByte = pcm[i]; continue; }
                ring[(head + count) % ring.Length] = (short)(lowByte | (pcm[i] << 8)) / 32768f;
                count++; lowByte = -1;
            }
        }
    }
    public void Complete() { lock (gate) { completed = true; lowByte = -1; } }
    public void Clear()
    {
        lock (gate)
        {
            head = count = 0; phase = 0; completed = running = false;
            gain = lastOutput = 0; lowByte = -1;
        }
    }

    // 音声スレッド。読み出し中に割り当てを行わず、24kHz→出力レートの変換も連続する。
    public int Render(float[] output, int channels, int outputRate)
    {
        if (channels <= 0 || outputRate <= 0) throw new ArgumentOutOfRangeException();
        int activeFrames = 0;
        double step = (double)inputRate / outputRate;
        float fadeStep = 1f / Math.Max(1, outputRate / 200); // 開始・終了だけ5ms。チャンク境界ではフェードしない。
        lock (gate)
        {
            for (int i = 0; i < output.Length; i += channels)
            {
                if (!running && (count >= prebufferSamples || (completed && count > 0)))
                { running = true; gain = 0; }
                float value;
                if (running && (count >= 2 || (completed && count > 0)))
                {
                    float a = ring[head];
                    float b = count > 1 ? ring[(head + 1) % ring.Length] : a;
                    value = a + (b - a) * (float)phase;
                    gain = Math.Min(1f, gain + fadeStep);
                    float tailGain = completed ? Math.Min(1f, (float)((count - phase) / inputRate / .005)) : 1f;
                    value *= gain * tailGain;
                    phase += step;
                    int consume = Math.Min(count, (int)phase);
                    phase -= consume; head = (head + consume) % ring.Length; count -= consume;
                    if (count == 0) phase = 0;
                    activeFrames++; renderedFrames++;
                }
                else
                {
                    if (running)
                    {
                        if (!completed) underruns++;
                        running = false; gain = 0;
                    }
                    // 通信が長く途切れた場合も、直前の振幅から急に0へ落とさない。
                    value = Math.Abs(lastOutput) <= fadeStep ? 0f : lastOutput - Math.Sign(lastOutput) * fadeStep;
                    if (value != 0f) activeFrames++;
                }
                lastOutput = value;
                for (int c = 0; c < channels && i + c < output.Length; c++) output[i + c] = value;
            }
        }
        return activeFrames;
    }
}
