// ImageToImage.cs
// カメラの今の1フレームと短い指示を Gemini に送り、変換後の絵を After に出すデモの本体。
//
// 上からの流れ:
//   Start → APIキー読込・UI 初期化・WebCam 起動
//   変換ボタン / Enter → 今のフレームを JPEG 化 → StartCoroutine(SendImageCoroutine)
//     → リクエストJSON組み立て（parts に text と inlineData、responseModalities に IMAGE）
//     → UnityWebRequest で POST → yield で応答待ち（このあいだ Status 点滅）
//     → inlineData の Base64 を Texture2D にして After に表示
//
// 会話履歴は送らない（毎回、今の1フレーム＋今の指示）。連続変換と Live API は使わない。

using System;
using System.Collections;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.Networking;
using UnityEngine.UI;

/// <summary>
/// Gemini generateContent でカメラ1フレームを変換し、After に出す。
/// </summary>
public class ImageToImage : MonoBehaviour
{
    // ===== インスペクタ: 設定 =====

    public string modelName = "gemini-3.1-flash-image"; // 使う画像生成モデル名（URL の一部になる）
    public string apiKeyRelativePath = "Common/APIKey.txt"; // Assets/ からの相対パス
    public string aspectRatio = "1:1"; // generationConfig.imageConfig.aspectRatio
    public string imageSize = "1K"; // generationConfig.imageConfig.imageSize（512 / 1K / 2K / 4K）
    public int maxSendLongSide = 768; // 送信 JPEG の長辺上限（4 と同じ推奨解像度）
    public int jpegQuality = 75; // EncodeToJPG の品質（1〜100）
    public string defaultPrompt = "この写真を、はっきりしたイラストにしてください"; // 入力欄の初期指示

    // ===== インスペクタ: 体験 UI（Camera / After ＋入力） =====

    public RawImage webcamPreview; // WebCam のライブ表示
    public RawImage resultImage; // 変換後画像（After）
    public TMP_Text emptyHintText; // After にまだ画像が無いときの案内
    public TMP_Text captionText; // 応答のテキスト part（あれば）
    public TMP_InputField inputField; // 指示テキスト
    public Button sendButton; // 変換ボタン
    public TMP_Text statusText; // 待機中 / 送信中 / 応答待ち などの状態

    // ===== 内部状態 =====

    string apiKey; // Assets/Common/APIKey.txt から読んだキー（画面には出さない）
    bool isSending; // 二重送信防止
    bool statusBlink; // 応答待ちのとき Status を点滅させる
    bool hasCamera; // WebCam を起動できたか
    WebCamTexture webCamTexture; // プレビュー兼キャプチャ源
    Texture2D generatedTexture; // いま After に出している変換画像（再変換で差し替え）

    const float StatusBlinkSpeed = 6f; // 点滅の速さ（大きいほど速い）
    const int WebcamRequestWidth = 1280; // WebCam 要求解像度（プレビュー用）
    const int WebcamRequestHeight = 720;

    static readonly Color BackgroundColor = new Color(0.12f, 0.12f, 0.14f, 1f); // カメラ背景
    static readonly Color ImageWellColor = new Color(0.08f, 0.09f, 0.11f, 1f); // 画像が無いときの枠色

    // ----- エントリポイント -----

    // 起動時: キーを読み、UI・WebCam・初期表示を用意する
    void Start()
    {
        LoadApiKey();
        EnsureRuntimeBasics();
        WireInput();
        ShowEmptyAfter();
        SetupWebcam();

        if (inputField != null && string.IsNullOrEmpty(inputField.text) && !string.IsNullOrEmpty(defaultPrompt))
        {
            inputField.text = defaultPrompt;
        }

        SetStatus(hasCamera ? "待機中" : "カメラがありません", false);
        SetSending(false);
    }

    // 応答待ち中だけ Status を点滅させる
    void Update()
    {
        UpdateStatusBlink();
    }

    // WebCam と生成テクスチャを解放する
    void OnDestroy()
    {
        StopWebcam();
        ReleaseGeneratedTexture();
    }

    // 変換ボタン / Enter を購読する
    void WireInput()
    {
        if (sendButton != null)
        {
            sendButton.onClick.AddListener(OnConvertClicked);
        }

        if (inputField != null)
        {
            inputField.onSubmit.AddListener(OnPromptSubmit);
        }
    }

    // 指示欄で Enter（送信）されたとき
    void OnPromptSubmit(string _)
    {
        OnConvertClicked();
    }

    // 変換の入口。空の指示・カメラ未準備・二重送信は無視する
    void OnConvertClicked()
    {
        if (isSending)
        {
            return;
        }

        string prompt = GetPromptText();
        if (string.IsNullOrEmpty(prompt))
        {
            SetStatus("指示を入力してください", false);
            return;
        }

        if (string.IsNullOrEmpty(apiKey))
        {
            ShowError("APIキーが空です。Assets/Docs/gemini-ai-studio-setup.md を参照してください。");
            return;
        }

        byte[] jpegBytes;
        if (!TryCaptureJpeg(out jpegBytes))
        {
            ShowError("カメラ画像を取れませんでした。カメラの接続と権限を確認してください。");
            return;
        }

        StartCoroutine(SendImageCoroutine(prompt, jpegBytes));
    }

    // ----- 通信本体（コルーチン） -----

    // 指示とカメラ JPEG を送り、変換画像を After に出す
    // UnityWebRequest.SendWebRequest の完了まで yield するため、待ちのあいだ Status を点滅できる
    IEnumerator SendImageCoroutine(string prompt, byte[] jpegBytes)
    {
        SetSending(true);

        SetStatus("リクエスト作成中", false);
        string url = "https://generativelanguage.googleapis.com/v1beta/models/"
                     + modelName
                     + ":generateContent";
        string jpegBase64 = Convert.ToBase64String(jpegBytes);
        string requestJson = BuildRequestJson(prompt, jpegBase64);

        SetStatus("送信中", false);
        byte[] bodyRaw = Encoding.UTF8.GetBytes(requestJson);
        using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json; charset=utf-8");
            // Docs と同じ認証ヘッダ。キー自体は画面に出さない
            request.SetRequestHeader("x-goog-api-key", apiKey);

            SetStatus("応答待ち", true);
            float sendStarted = Time.realtimeSinceStartup; // 送信開始。返信までの計測用
            yield return request.SendWebRequest();
            ResponseTime.Log("合計", sendStarted);

            string responseBody = request.downloadHandler != null
                ? request.downloadHandler.text
                : string.Empty;

            SetStatus("応答解析中", false);

            if (request.result != UnityWebRequest.Result.Success)
            {
                ShowError("HTTP エラー: " + request.responseCode + " / " + request.error);
                SetSending(false);
                yield break;
            }

            string mimeType;
            string imageBase64;
            string caption;
            if (!TryExtractInlineImage(responseBody, out mimeType, out imageBase64, out caption))
            {
                ShowError("応答から画像を取り出せませんでした。");
                SetSending(false);
                yield break;
            }

            if (!TryShowGeneratedImage(imageBase64, mimeType))
            {
                SetSending(false);
                yield break;
            }

            SetCaption(caption);
            SetStatus("完了", false);
        }

        SetSending(false);
    }

    // ----- リクエスト JSON -----

    // 今の指示とカメラ JPEG を parts に並べ、画像を返す設定を載せる
    // 形: {"contents":[{"role":"user","parts":[{"text":...},{"inlineData":...}]}],"generationConfig":{...}}
    string BuildRequestJson(string prompt, string jpegBase64)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("{\"contents\":[{\"role\":\"user\",\"parts\":[");
        sb.Append("{\"text\":\"");
        sb.Append(GeminiJson.Escape(prompt));
        sb.Append("\"},");
        // 参照画像。mime は JPEG。data は Base64（画面表示だけ後で短縮する）
        sb.Append("{\"inlineData\":{\"mimeType\":\"image/jpeg\",\"data\":\"");
        sb.Append(jpegBase64);
        sb.Append("\"}}]}],\"generationConfig\":{");
        sb.Append("\"responseModalities\":[\"TEXT\",\"IMAGE\"]");

        // 画面には出さないが、Request ペインで縦横比と解像度が追えるように載せる
        if (!string.IsNullOrEmpty(aspectRatio) || !string.IsNullOrEmpty(imageSize))
        {
            sb.Append(",\"imageConfig\":{");
            bool wrote = false;
            if (!string.IsNullOrEmpty(aspectRatio))
            {
                sb.Append("\"aspectRatio\":\"");
                sb.Append(GeminiJson.Escape(aspectRatio));
                sb.Append('"');
                wrote = true;
            }

            if (!string.IsNullOrEmpty(imageSize))
            {
                if (wrote)
                {
                    sb.Append(',');
                }

                sb.Append("\"imageSize\":\"");
                sb.Append(GeminiJson.Escape(imageSize));
                sb.Append('"');
            }

            sb.Append('}');
        }

        sb.Append("}}");
        return sb.ToString();
    }

    // ----- レスポンス解析 -----

    // JsonUtility で入れ子 DTO に載せ、最初の画像 part とテキスト part を返す
    bool TryExtractInlineImage(
        string responseBody,
        out string mimeType,
        out string imageBase64,
        out string caption)
    {
        mimeType = null;
        imageBase64 = null;
        caption = null;
        if (string.IsNullOrEmpty(responseBody))
        {
            return false;
        }

        // REST は camelCase。公式例の snake_case が来ても読めるようにキーだけ揃える
        string normalized = NormalizeInlineDataKeys(responseBody);
        GeminiResponse parsed = null;
        try
        {
            parsed = JsonUtility.FromJson<GeminiResponse>(normalized);
        }
        catch (Exception e)
        {
            Debug.LogError("[ImageToImage] JSON 解析失敗: " + e.Message);
            return false;
        }

        if (parsed == null || parsed.candidates == null || parsed.candidates.Length == 0)
        {
            return false;
        }

        GeminiCandidate first = parsed.candidates[0];
        if (first == null || first.content == null || first.content.parts == null)
        {
            return false;
        }

        for (int i = 0; i < first.content.parts.Length; i++)
        {
            GeminiPart part = first.content.parts[i];
            if (part == null)
            {
                continue;
            }

            if (caption == null && !string.IsNullOrEmpty(part.text))
            {
                caption = part.text.Trim();
            }

            if (imageBase64 != null || part.inlineData == null || string.IsNullOrEmpty(part.inlineData.data))
            {
                continue;
            }

            mimeType = part.inlineData.mimeType != null ? part.inlineData.mimeType : string.Empty;
            imageBase64 = part.inlineData.data;
        }

        return !string.IsNullOrEmpty(imageBase64);
    }

    // inline_data / mime_type を JsonUtility が読める名前に揃える
    static string NormalizeInlineDataKeys(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return json;
        }

        return json.Replace("\"inline_data\"", "\"inlineData\"").Replace("\"mime_type\"", "\"mimeType\"");
    }

    [Serializable]
    class GeminiResponse
    {
        public GeminiCandidate[] candidates;
    }

    [Serializable]
    class GeminiCandidate
    {
        public GeminiContent content;
    }

    [Serializable]
    class GeminiContent
    {
        public GeminiPart[] parts;
    }

    [Serializable]
    class GeminiPart
    {
        public string text;
        public GeminiInlineData inlineData; // 画像（text と並ぶことがある）
    }

    [Serializable]
    class GeminiInlineData
    {
        public string mimeType; // 例: image/png
        public string data; // Base64 の画像バイト列
    }

    // Base64 を Texture2D にして After の RawImage に載せる
    bool TryShowGeneratedImage(string imageBase64, string mimeType)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(imageBase64);
        }
        catch (Exception e)
        {
            ShowError("画像の Base64 をデコードできませんでした: " + e.Message);
            return false;
        }

        Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!texture.LoadImage(bytes))
        {
            Destroy(texture);
            ShowError("画像バイトを Texture2D にできませんでした（mime=" + mimeType + "）。");
            return false;
        }

        ReleaseGeneratedTexture();
        generatedTexture = texture;
        if (resultImage != null)
        {
            resultImage.texture = generatedTexture;
            resultImage.color = Color.white;
        }

        if (emptyHintText != null)
        {
            emptyHintText.gameObject.SetActive(false);
        }

        return true;
    }

    void ReleaseGeneratedTexture()
    {
        if (generatedTexture == null)
        {
            return;
        }

        if (resultImage != null && resultImage.texture == generatedTexture)
        {
            resultImage.texture = null;
        }

        Destroy(generatedTexture);
        generatedTexture = null;
    }

    // ----- WebCam -----

    // 先頭デバイスを開き、左の Camera にライブ表示する
    void SetupWebcam()
    {
        hasCamera = false;
        if (WebCamTexture.devices == null || WebCamTexture.devices.Length == 0)
        {
            webCamTexture = null;
            Debug.LogWarning("[ImageToImage] カメラがありません。");
            if (webcamPreview != null)
            {
                webcamPreview.texture = null;
                webcamPreview.color = ImageWellColor;
            }

            return;
        }

        string deviceName = WebCamTexture.devices[0].name;
        webCamTexture = new WebCamTexture(deviceName, WebcamRequestWidth, WebcamRequestHeight, 30);
        webCamTexture.Play();
        hasCamera = true;
        if (webcamPreview != null)
        {
            webcamPreview.texture = webCamTexture;
            webcamPreview.color = Color.white;
        }

        Debug.Log("[ImageToImage] カメラ: " + deviceName);
    }

    void StopWebcam()
    {
        if (webcamPreview != null && webcamPreview.texture == webCamTexture)
        {
            webcamPreview.texture = null;
        }

        if (webCamTexture != null)
        {
            if (webCamTexture.isPlaying)
            {
                webCamTexture.Stop();
            }

            Destroy(webCamTexture);
            webCamTexture = null;
        }

        hasCamera = false;
    }

    // WebCam の現フレームを長辺制限つき JPEG にする（4.VisionToSpeech と同じ手順）
    bool TryCaptureJpeg(out byte[] jpegBytes)
    {
        jpegBytes = null;

        if (webCamTexture == null || !webCamTexture.isPlaying || webCamTexture.width < 16)
        {
            return false;
        }

        Texture2D src = new Texture2D(webCamTexture.width, webCamTexture.height, TextureFormat.RGB24, false);
        src.SetPixels(webCamTexture.GetPixels());
        src.Apply();

        int srcW = src.width;
        int srcH = src.height;
        int longSide = Mathf.Max(srcW, srcH);
        Texture2D sendTex = src;
        if (longSide > maxSendLongSide && maxSendLongSide > 0)
        {
            float scale = (float)maxSendLongSide / longSide;
            int dstW = Mathf.Max(1, Mathf.RoundToInt(srcW * scale));
            int dstH = Mathf.Max(1, Mathf.RoundToInt(srcH * scale));
            sendTex = TextureUtil.Scale(src, dstW, dstH);
            Destroy(src);
        }

        jpegBytes = sendTex.EncodeToJPG(Mathf.Clamp(jpegQuality, 1, 100));
        Destroy(sendTex);
        return jpegBytes != null && jpegBytes.Length > 0;
    }

    // ----- APIキー -----

    // Assets/Common/APIKey.txt を1行読む（リポジトリにはコミットしない）
    void LoadApiKey()
    {
        string error;
        if (!GeminiKey.TryRead(apiKeyRelativePath, out apiKey, out error))
        {
            Debug.LogError("[ImageToImage] " + error);
            return;
        }

        Debug.Log("[ImageToImage] APIキーを読み込みました（長さ " + apiKey.Length + "）。キー自体はログに出しません。");
    }

    // ----- UI 更新 -----

    void SetStatus(string statusJapanese, bool blink)
    {
        statusBlink = blink;
        if (statusText == null)
        {
            return;
        }

        statusText.text = statusJapanese;
        Color color = statusText.color;
        color.a = 1f;
        statusText.color = color;
    }

    void UpdateStatusBlink()
    {
        if (!statusBlink || statusText == null)
        {
            return;
        }

        float wave = (Mathf.Sin(Time.unscaledTime * StatusBlinkSpeed) + 1f) * 0.5f;
        Color color = statusText.color;
        color.a = Mathf.Lerp(0.25f, 1f, wave);
        statusText.color = color;
    }

    void ShowError(string message)
    {
        Debug.LogError("[ImageToImage] " + message);
        SetStatus(message, false);
    }

    void SetCaption(string caption)
    {
        if (captionText == null)
        {
            return;
        }

        captionText.text = caption != null ? caption : string.Empty;
    }

    void ShowEmptyAfter()
    {
        if (resultImage != null)
        {
            resultImage.texture = null;
            resultImage.color = ImageWellColor;
        }

        if (emptyHintText != null)
        {
            emptyHintText.gameObject.SetActive(true);
            emptyHintText.text = "まだ画像がありません";
        }

        SetCaption(string.Empty);
    }

    void SetSending(bool sending)
    {
        isSending = sending;
        if (sendButton != null)
        {
            sendButton.interactable = !sending;
        }

        if (inputField != null)
        {
            inputField.interactable = !sending;
        }
    }

    string GetPromptText()
    {
        if (inputField == null || inputField.text == null)
        {
            return string.Empty;
        }

        return inputField.text.Trim();
    }

    // EventSystem が無ければ足し、カメラ背景をシーンの色に合わせる
    void EnsureRuntimeBasics()
    {
        EnsureEventSystem();
        if (Camera.main != null)
        {
            Camera.main.backgroundColor = BackgroundColor;
            Camera.main.clearFlags = CameraClearFlags.SolidColor;
        }
    }

    // シーンに EventSystem が無いときだけ作る
    void EnsureEventSystem()
    {
        if (EventSystem.current != null)
        {
            return;
        }

        GameObject es = new GameObject("EventSystem");
        es.AddComponent<EventSystem>();
        es.AddComponent<InputSystemUIInputModule>();
    }
}
