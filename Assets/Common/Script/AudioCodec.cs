// AudioCodec.cs
// 音声データの形を変える道具。マイクの制御や再生そのものは各デモが持つ。
//
// 扱う形は3つ:
//   AudioClip    … Unity が録音・再生に使う形
//   WAV バイト列 … Gemini の REST に inlineData として送る形（ヘッダ + 16bit PCM）
//   PCM16 バイト列 … Live API が送受信する生の音声（16bit・リトルエンディアン）
//
// 使っているデモ: 2A / 2B / 2C / 3A / 3B / 3C / 4 / 5

using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// AudioClip と WAV / PCM16 を相互に変換する道具。状態を持たない。
/// </summary>
public static class AudioCodec
{
    // Microphone が書き込んだ先頭 positionSamples だけを新しい AudioClip にコピーする
    public static AudioClip TrimClip(AudioClip source, int positionSamples)
    {
        if (source == null || positionSamples <= 0)
        {
            return null;
        }

        int channels = source.channels;
        int copySamples = Mathf.Min(positionSamples, source.samples);
        float[] data = new float[copySamples * channels];
        if (!source.GetData(data, 0))
        {
            return null;
        }

        AudioClip trimmed = AudioClip.Create(
            "RecordingTrimmed",
            copySamples,
            channels,
            source.frequency,
            false);
        trimmed.SetData(data, 0);
        return trimmed;
    }

    // Microphone が書き込んだ先頭 positionSamples を float 配列で取り出す（端末 STT 用）
    // 複数チャネルなら平均して 1ch にする
    public static float[] CopyClipSamples(AudioClip source, int positionSamples)
    {
        if (source == null || positionSamples <= 0)
        {
            return null;
        }

        int channels = source.channels;
        int copySamples = Mathf.Min(positionSamples, source.samples);
        float[] data = new float[copySamples * channels];
        if (!source.GetData(data, 0))
        {
            return null;
        }

        if (channels <= 1)
        {
            return data;
        }

        float[] mono = new float[copySamples];
        for (int i = 0; i < copySamples; i++)
        {
            float sum = 0f;
            for (int c = 0; c < channels; c++)
            {
                sum += data[i * channels + c];
            }

            mono[i] = sum / channels;
        }

        return mono;
    }

    // AudioClip → WAV（ヘッダ + 16-bit PCM）。Gemini inlineData 用のバイト列を作る
    public static byte[] ClipToWav(AudioClip clip)
    {
        if (clip == null)
        {
            return null;
        }

        int sampleCount = clip.samples * clip.channels;
        float[] samples = new float[sampleCount];
        clip.GetData(samples, 0);

        short[] pcm = new short[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            float clamped = Mathf.Clamp(samples[i], -1f, 1f);
            pcm[i] = (short)Mathf.RoundToInt(clamped * short.MaxValue);
        }

        int byteRate = clip.frequency * clip.channels * 2;
        int dataSize = pcm.Length * 2;
        using (MemoryStream stream = new MemoryStream(44 + dataSize))
        using (BinaryWriter writer = new BinaryWriter(stream))
        {
            // RIFF / WAVE ヘッダ（PCM）
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataSize);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((ushort)1); // PCM
            writer.Write((ushort)clip.channels);
            writer.Write(clip.frequency);
            writer.Write(byteRate);
            writer.Write((ushort)(clip.channels * 2));
            writer.Write((ushort)16);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataSize);

            for (int i = 0; i < pcm.Length; i++)
            {
                writer.Write(pcm[i]);
            }

            return stream.ToArray();
        }
    }

    // マイクの float サンプル → PCM16 バイト列（Live API へ送る形）
    public static byte[] FloatsToPcm16(float[] samples)
    {
        byte[] pcm = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            float clamped = Mathf.Clamp(samples[i], -1f, 1f);
            short s = (short)Mathf.RoundToInt(clamped * short.MaxValue);
            pcm[i * 2] = (byte)(s & 0xff);
            pcm[i * 2 + 1] = (byte)((s >> 8) & 0xff);
        }

        return pcm;
    }

    // Live API から届いた PCM16 バイト列 → 再生できる AudioClip（モノラル）
    public static AudioClip Pcm16ToClip(byte[] pcm, int rate)
    {
        if (pcm == null || pcm.Length < 2 || rate <= 0)
        {
            return null;
        }

        int sampleCount = pcm.Length / 2;
        float[] samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            short s = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            samples[i] = s / 32768f;
        }

        AudioClip clip = AudioClip.Create("LivePcm", sampleCount, 1, rate, false);
        clip.SetData(samples, 0);
        return clip;
    }
}
