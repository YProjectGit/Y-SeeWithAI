// TextureUtil.cs
// テクスチャの縮小。カメラや画面のフレームを送る前に、送信サイズを小さくするために使う。
//
// 使っているデモ: 4 / 5 / 7

using UnityEngine;

/// <summary>
/// Texture2D を指定サイズに縮小する道具。状態を持たない。
/// </summary>
public static class TextureUtil
{
    // バイリニア補間で dstW × dstH に縮小した新しいテクスチャを返す
    public static Texture2D Scale(Texture2D source, int dstW, int dstH)
    {
        Texture2D dst = new Texture2D(dstW, dstH, TextureFormat.RGB24, false);
        for (int y = 0; y < dstH; y++)
        {
            float v = (y + 0.5f) / dstH;
            for (int x = 0; x < dstW; x++)
            {
                float u = (x + 0.5f) / dstW;
                dst.SetPixel(x, y, source.GetPixelBilinear(u, v));
            }
        }

        dst.Apply();
        return dst;
    }
}
