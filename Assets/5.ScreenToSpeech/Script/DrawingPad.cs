// DrawingPad.cs
// マウスで白い紙に線を描くだけの部品。通信や Live API は持たない。
//
// 上からの流れ:
//   Awake → 白い Texture2D を用意して RawImage に載せる
//   ポインタ Down/Drag → 直前の点から今の点まで円を並べて線にする
//   Clear → 紙を白紙に戻す（インクなし・dirty なし）

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// マウスドラッグで Texture2D に描くキャンバス。送信判定用にインク有無と dirty を持つ。
/// </summary>
public class DrawingPad : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
{
    public int textureWidth = 768; // 紙の横ピクセル（送信 JPEG の目安と同じ）
    public int textureHeight = 768; // 紙の縦ピクセル
    public Color paperColor = new Color(0.97f, 0.95f, 0.92f, 1f); // 紙の色（オフホワイト）
    public Color inkColor = new Color(0.10f, 0.10f, 0.12f, 1f); // インクの色
    public int brushRadius = 6; // 筆の半径（ピクセル）

    Texture2D canvasTexture; // 紙そのもの。表示と JPEG 化の両方に使う
    Color[] pixels; // SetPixels 用の作業配列（毎回 new しない）
    RawImage rawImage; // 画面にテクスチャを出す先
    Vector2 lastPixel; // 直前の描画位置（線を繋ぐため）
    bool hasLastPixel; // lastPixel が有効か
    bool hasInk; // 1ドットでも描いたか（空送信防止）
    bool dirty; // 前回送信以降に描き足したか

    /// <summary>紙のテクスチャ。JPEG 化の入力。</summary>
    public Texture2D CanvasTexture
    {
        get { return canvasTexture; }
    }

    /// <summary>インクが1ドットでもあるか。空の紙は送らない。</summary>
    public bool HasInk
    {
        get { return hasInk; }
    }

    /// <summary>前回 MarkClean してから描き足したか。</summary>
    public bool IsDirty
    {
        get { return dirty; }
    }

    // 起動時に紙を1枚用意する
    void Awake()
    {
        EnsureCanvas();
    }

    // 破棄時にテクスチャを解放する
    void OnDestroy()
    {
        if (canvasTexture != null)
        {
            Destroy(canvasTexture);
            canvasTexture = null;
        }
    }

    // ポインタを下ろした位置に点を打ち、線の始点にする
    public void OnPointerDown(PointerEventData eventData)
    {
        Vector2 pixel;
        if (!TryScreenToPixel(eventData, out pixel))
        {
            return;
        }

        StampLine(pixel, pixel);
        lastPixel = pixel;
        hasLastPixel = true;
    }

    // 直前の点から今の点まで円を並べ、途切れない線にする
    public void OnDrag(PointerEventData eventData)
    {
        Vector2 pixel;
        if (!TryScreenToPixel(eventData, out pixel))
        {
            return;
        }

        if (hasLastPixel)
        {
            StampLine(lastPixel, pixel);
        }
        else
        {
            StampLine(pixel, pixel);
        }

        lastPixel = pixel;
        hasLastPixel = true;
    }

    // ポインタを離したら、次の線の始点を忘れる
    public void OnPointerUp(PointerEventData eventData)
    {
        hasLastPixel = false;
    }

    /// <summary>
    /// 紙を白紙に戻す。インクも dirty も消える。
    /// </summary>
    public void Clear()
    {
        EnsureCanvas();
        FillPaper();
        canvasTexture.Apply();
        hasInk = false;
        dirty = false;
        hasLastPixel = false;
    }

    /// <summary>
    /// いまの紙を送信済みとして dirty を下ろす。
    /// </summary>
    public void MarkClean()
    {
        dirty = false;
    }

    /// <summary>
    /// いまの紙を JPEG バイトにする。失敗時は null。
    /// </summary>
    public byte[] EncodeToJpg(int quality)
    {
        EnsureCanvas();
        canvasTexture.Apply();
        return canvasTexture.EncodeToJPG(Mathf.Clamp(quality, 1, 100));
    }

    // テクスチャと RawImage が未準備なら作る
    void EnsureCanvas()
    {
        if (canvasTexture != null)
        {
            return;
        }

        int width = Mathf.Max(16, textureWidth);
        int height = Mathf.Max(16, textureHeight);
        canvasTexture = new Texture2D(width, height, TextureFormat.RGB24, false);
        canvasTexture.filterMode = FilterMode.Bilinear;
        canvasTexture.wrapMode = TextureWrapMode.Clamp;
        pixels = new Color[width * height];
        FillPaper();
        canvasTexture.Apply();

        rawImage = GetComponent<RawImage>();
        if (rawImage != null)
        {
            rawImage.texture = canvasTexture;
            rawImage.color = Color.white;
        }
    }

    // 作業配列を紙色で埋める
    void FillPaper()
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = paperColor;
        }

        canvasTexture.SetPixels(pixels);
    }

    // 画面座標をテクスチャのピクセル座標へ変換する（左下原点）
    bool TryScreenToPixel(PointerEventData eventData, out Vector2 pixel)
    {
        pixel = Vector2.zero;
        RectTransform rt = transform as RectTransform;
        if (rt == null)
        {
            return false;
        }

        Vector2 local;
        Camera cam = eventData.pressEventCamera;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, eventData.position, cam, out local))
        {
            return false;
        }

        Rect rect = rt.rect;
        if (rect.width <= 0f || rect.height <= 0f)
        {
            return false;
        }

        float u = (local.x - rect.xMin) / rect.width;
        float v = (local.y - rect.yMin) / rect.height;
        if (u < 0f || u > 1f || v < 0f || v > 1f)
        {
            return false;
        }

        pixel = new Vector2(u * (canvasTexture.width - 1), v * (canvasTexture.height - 1));
        return true;
    }

    // from から to まで円スタンプを重ね、ドラッグの飛びを埋める
    void StampLine(Vector2 from, Vector2 to)
    {
        float dist = Vector2.Distance(from, to);
        float step = Mathf.Max(1f, brushRadius * 0.5f);
        int count = Mathf.Max(1, Mathf.CeilToInt(dist / step));
        for (int i = 0; i <= count; i++)
        {
            float t = count == 0 ? 0f : i / (float)count;
            Vector2 p = Vector2.Lerp(from, to, t);
            Stamp(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y));
        }

        canvasTexture.SetPixels(pixels);
        canvasTexture.Apply();
        hasInk = true;
        dirty = true;
    }

    // 中心 (cx, cy) にインクの円を1つ置く
    void Stamp(int cx, int cy)
    {
        int width = canvasTexture.width;
        int height = canvasTexture.height;
        int radius = Mathf.Max(1, brushRadius);
        int radiusSq = radius * radius;
        int minX = Mathf.Max(0, cx - radius);
        int maxX = Mathf.Min(width - 1, cx + radius);
        int minY = Mathf.Max(0, cy - radius);
        int maxY = Mathf.Min(height - 1, cy + radius);

        for (int y = minY; y <= maxY; y++)
        {
            int dy = y - cy;
            for (int x = minX; x <= maxX; x++)
            {
                int dx = x - cx;
                if (dx * dx + dy * dy <= radiusSq)
                {
                    pixels[y * width + x] = inkColor;
                }
            }
        }
    }
}
