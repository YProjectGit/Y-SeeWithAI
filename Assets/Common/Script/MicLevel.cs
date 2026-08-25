// MicLevel.cs
// マイク音量の計算だけ。Microphone の開始／停止と横棒 UI は各デモが持つ。
//
// 使う流れ:
//   デモが AudioClip と Microphone.GetPosition を渡す
//   → 直近窓の RMS（大きさの目安）
//   → 0〜1 の棒の長さ
//   → Smooth で上がりはすぐ、下りはゆっくり
//
// 使っているデモ: 2A / 2B / 2C / 3A / 3B / 3C

using UnityEngine;

/// <summary>
/// マイクサンプルからレベルメーター用の 0〜1 を出す道具。状態を持たない。
/// </summary>
public static class MicLevel
{
    public const int WindowSamples = 1024; // 直近サンプル数（16 kHz で約 64 ms）
    public const float Gain = 6f; // RMS を 0〜1 に伸ばす倍率（小さい声でも棒が見えるように）
    public const float ReleasePerSecond = 2f; // 無音になったとき棒が戻る速さ

    // 直近窓の大きさを 0〜1 の棒の長さにする。窓が読めないときは keep
    public static float ReadBar(AudioClip clip, int micPosition, float[] buffer, bool looped, float keep)
    {
        if (clip == null || buffer == null || buffer.Length == 0)
        {
            return 0f;
        }

        if (!TryCopyWindow(clip, micPosition, buffer, looped))
        {
            return keep;
        }

        return ToBar(Rms(buffer));
    }

    // 上がりはすぐ反映し、下りだけ一定速度で戻す
    public static float Smooth(float displayed, float target, float deltaTime)
    {
        if (target > displayed)
        {
            return target;
        }

        return Mathf.MoveTowards(displayed, target, deltaTime * ReleasePerSecond);
    }

    // マイク書き込み位置の直前から buffer 長さぶんコピーする。looped なら先頭巻き戻りを許す
    public static bool TryCopyWindow(AudioClip clip, int micPosition, float[] buffer, bool looped)
    {
        if (clip == null || buffer == null || buffer.Length == 0 || micPosition < 0)
        {
            return false;
        }

        int window = buffer.Length;
        int start = micPosition - window;
        if (start < 0)
        {
            if (!looped)
            {
                return false;
            }

            start += clip.samples;
        }

        if (start < 0 || start + window > clip.samples)
        {
            return false;
        }

        return clip.GetData(buffer, start);
    }

    // 二乗平均平方根（RMS）。サンプルの大きさの目安
    public static float Rms(float[] samples)
    {
        if (samples == null || samples.Length == 0)
        {
            return 0f;
        }

        float sumSquares = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            float sample = samples[i];
            sumSquares += sample * sample;
        }

        return Mathf.Sqrt(sumSquares / samples.Length);
    }

    // RMS を横棒の 0〜1 にする
    public static float ToBar(float rms)
    {
        return Mathf.Clamp01(rms * Gain);
    }
}
