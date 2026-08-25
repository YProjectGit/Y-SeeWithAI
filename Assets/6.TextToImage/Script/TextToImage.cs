// TextToImage.cs
// Gemini にテキスト（プロンプト）を送り、返ってきた画像を左に出すデモの本体。
// 中央に Request、右に Response の生データを出し、通信の流れを追えるようにする。
//
// 上からの流れ:
//   Start → APIキー読込・UI 初期化
//   送信ボタン / Enter → StartCoroutine(SendImageCoroutine)
//     → リクエストJSON組み立て（responseModalities に IMAGE）→ UnityWebRequest で POST
//     → yield で応答待ち（このあいだ Status 点滅）→ 生JSON表示
//     → inlineData の Base64 を Texture2D にして左に表示
//
// 会話履歴は送らない（毎回、いまのプロンプトだけ）。続きの編集は 7.ImageToImage。

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
/// Gemini generateContent でテキストから画像を1枚生成し、送受信の生データを可視化する。
/// </summary>
public class TextToImage : MonoBehaviour
{
    // ===== インスペクタ: 設定 =====

    public string modelName = "gemini-3.1-flash-image"; // 使う画像生成モデル名（URL の一部になる）
    public string apiKeyRelativePath = "Common/APIKey.txt"; // Assets/ からの相対パス
    public string aspectRatio = "1:1"; // generationConfig.imageConfig.aspectRatio
    public string imageSize = "1K"; // generationConfig.imageConfig.imageSize（512 / 1K / 2K / 4K）

    // ===== インスペクタ: 左ペイン（結果＋入力） =====

    public RawImage resultImage; // 生成画像の表示先
    public TMP_Text emptyHintText; // まだ画像が無いときの案内
    public TMP_Text captionText; // 応答のテキスト part（あれば）
    public TMP_InputField inputField; // プロンプト入力
    public Button sendButton; // 送信ボタン
    public TMP_Text statusText; // 待機中 / 送信中 / 応答待ち などの状態

    // ===== インスペクタ: 可視化（中央 Request / 右 Response） =====

    public TMP_Text requestText; // URL・ヘッダ（キーはマスク）・リクエスト JSON
    public TMP_Text responseText; // HTTP ステータス + 要約 + 短縮した生 JSON

    // ===== 内部状態 =====

    string apiKey; // Assets/Common/APIKey.txt から読んだキー（画面には出さない）
    bool isSending; // 二重送信防止
    bool statusBlink; // 応答待ちのとき Status を点滅させる
    Texture2D generatedTexture; // いま左に出している生成画像（再送信で差し替え）

    const float StatusBlinkSpeed = 6f; // 点滅の速さ（大きいほど速い）
    const int DisplayBase64MaxChars = 96; // 画面では Base64 をこの長さまで

    static readonly Color BackgroundColor = new Color(0.12f, 0.12f, 0.14f, 1f); // カメラ背景
    static readonly Color ImageWellColor = new Color(0.08f, 0.09f, 0.11f, 1f); // 画像が無いときの枠色

    // ----- エントリポイント -----

    // 起動時: キーを読み、UI と初期表示を用意する
    void Start()
    {
        LoadApiKey();
        EnsureRuntimeBasics();
        WireInput();
        ShowEmptyPreview();

        SetStatus("待機中", false);
        if (requestText != null)
        {
            requestText.text = "（まだ送信していません）";
        }

        if (responseText != null)
        {
            responseText.text = "（まだ応答がありません）";
        }

        SetSending(false);
    }

    // 応答待ち中だけ Status を点滅させる
    void Update()
    {
        UpdateStatusBlink();
    }

    // 生成テクスチャを解放する
    void OnDestroy()
    {
        ReleaseGeneratedTexture();
    }

    // 送信ボタン / Enter を購読する
    void WireInput()
    {
        if (sendButton != null)
        {
            sendButton.onClick.AddListener(OnSendClicked);
        }

        if (inputField != null)
        {
            inputField.onSubmit.AddListener(OnPromptSubmit);
        }
    }

    // Message 欄で Enter（送信）されたとき
    void OnPromptSubmit(string _)
    {
        OnSendClicked();
    }

    // 送信の入口。空のプロンプトと二重送信は無視する
    void OnSendClicked()
    {
        if (isSending)
        {
            return;
        }

        string prompt = GetPromptText();
        if (string.IsNullOrEmpty(prompt))
        {
            SetStatus("プロンプトを入力してください", false);
            return;
        }

        if (string.IsNullOrEmpty(apiKey))
        {
            ShowError("APIキーが空です。Assets/Docs/gemini-ai-studio-setup.md を参照してください。");
            return;
        }

        StartCoroutine(SendImageCoroutine(prompt));
    }

    // ----- 通信本体（コルーチン） -----

    // プロンプトを送り、Gemini の画像を受け取って各ペインを更新する
    // UnityWebRequest.SendWebRequest の完了まで yield するため、待ちのあいだ Status を点滅できる
    IEnumerator SendImageCoroutine(string prompt)
    {
        SetSending(true);

        SetStatus("リクエスト作成中", false);
        string url = "https://generativelanguage.googleapis.com/v1beta/models/"
                     + modelName
                     + ":generateContent";
        string requestJson = BuildRequestJson(prompt);
        ShowRequest(url, requestJson);

        SetStatus("送信中", false);
        byte[] bodyRaw = Encoding.UTF8.GetBytes(requestJson);
        using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json; charset=utf-8");
            // Docs と同じ認証ヘッダ。キー自体は Request ペインではマスク表示する
            request.SetRequestHeader("x-goog-api-key", apiKey);

            SetStatus("応答待ち", true);
            float sendStarted = Time.realtimeSinceStartup; // 送信開始。返信までの計測用
            yield return request.SendWebRequest();
            ResponseTime.Log("合計", sendStarted);

            long statusCode = request.responseCode;
            string responseBody = request.downloadHandler != null
                ? request.downloadHandler.text
                : string.Empty;

            SetStatus("応答解析中", false);
            ShowResponse(statusCode, responseBody);

            if (request.result != UnityWebRequest.Result.Success)
            {
                ShowError("HTTP エラー: " + statusCode + " / " + request.error);
                SetSending(false);
                yield break;
            }

            string mimeType;
            string imageBase64;
            string caption;
            if (!TryExtractInlineImage(responseBody, out mimeType, out imageBase64, out caption))
            {
                ShowError("応答 JSON から画像を取り出せませんでした。Response ペインを確認してください。");
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

    // いまのプロンプト1件と、画像を返す設定を載せる
    // 形: {"contents":[...],"generationConfig":{"responseModalities":["TEXT","IMAGE"],"imageConfig":{...}}}
    string BuildRequestJson(string prompt)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("{\"contents\":[{\"role\":\"user\",\"parts\":[{\"text\":\"");
        sb.Append(GeminiJson.Escape(prompt));
        sb.Append("\"}]}],\"generationConfig\":{");
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
            Debug.LogError("[TextToImage] JSON 解析失敗: " + e.Message);
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

    // Base64 を Texture2D にして左の RawImage に載せる
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

    // ----- APIキー -----

    // Assets/Common/APIKey.txt を1行読む（リポジトリにはコミットしない）
    void LoadApiKey()
    {
        string error;
        if (!GeminiKey.TryRead(apiKeyRelativePath, out apiKey, out error))
        {
            Debug.LogError("[TextToImage] " + error);
            return;
        }

        Debug.Log("[TextToImage] APIキーを読み込みました（長さ " + apiKey.Length + "）。キー自体はログに出しません。");
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

    void ShowRequest(string url, string requestJson)
    {
        if (requestText == null)
        {
            return;
        }

        StringBuilder sb = new StringBuilder();
        sb.AppendLine("POST " + url);
        sb.AppendLine("Content-Type: application/json; charset=utf-8");
        sb.AppendLine("x-goog-api-key: " + GeminiKey.Mask(apiKey));
        sb.AppendLine();
        sb.Append(GeminiJson.PrettyPrint(requestJson));
        requestText.text = sb.ToString();
    }

    // 右ペイン: HTTP 番号、mime / バイト数、短縮した JSON
    void ShowResponse(long statusCode, string responseBody)
    {
        if (responseText == null)
        {
            return;
        }

        StringBuilder sb = new StringBuilder();
        sb.AppendLine("HTTP " + statusCode);

        string mimeType;
        string imageBase64;
        string caption;
        bool hasImage = TryExtractInlineImage(responseBody, out mimeType, out imageBase64, out caption);
        if (hasImage)
        {
            int byteLength = 0;
            try
            {
                byteLength = Convert.FromBase64String(imageBase64).Length;
            }
            catch (Exception)
            {
                byteLength = 0;
            }

            sb.AppendLine("mimeType: " + mimeType);
            sb.AppendLine("image bytes: " + byteLength);
            sb.AppendLine("text part: " + (!string.IsNullOrEmpty(caption) ? "yes" : "no"));
        }

        sb.AppendLine();
        sb.Append(string.IsNullOrEmpty(responseBody)
            ? "(empty body)"
            : GeminiJson.PrettyPrint(TruncateAllBase64ForDisplay(responseBody)));
        responseText.text = sb.ToString();
    }

    void ShowError(string message)
    {
        Debug.LogError("[TextToImage] " + message);
        SetStatus("エラー", false);
        if (responseText != null && !responseText.text.Contains(message))
        {
            responseText.text = responseText.text + "\n\n[Error]\n" + message;
        }
    }

    void SetCaption(string caption)
    {
        if (captionText == null)
        {
            return;
        }

        captionText.text = caption != null ? caption : string.Empty;
    }

    void ShowEmptyPreview()
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

    // ----- JSON / 表示ヘルパー -----

    // 応答 JSON 内の長い Base64 を、画面では先頭だけにする（送信自体は全文）
    static string TruncateAllBase64ForDisplay(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return json;
        }

        const string marker = "\"data\":\"";
        StringBuilder sb = new StringBuilder(json.Length);
        int searchFrom = 0;
        while (searchFrom < json.Length)
        {
            int dataIndex = json.IndexOf(marker, searchFrom, StringComparison.Ordinal);
            if (dataIndex < 0)
            {
                sb.Append(json.Substring(searchFrom));
                break;
            }

            int valueStart = dataIndex + marker.Length;
            int valueEnd = json.IndexOf('"', valueStart);
            if (valueEnd < 0)
            {
                sb.Append(json.Substring(searchFrom));
                break;
            }

            sb.Append(json.Substring(searchFrom, valueStart - searchFrom));
            int length = valueEnd - valueStart;
            if (length <= DisplayBase64MaxChars)
            {
                sb.Append(json.Substring(valueStart, length));
            }
            else
            {
                sb.Append(json.Substring(valueStart, DisplayBase64MaxChars));
                sb.Append("…(");
                sb.Append(length);
                sb.Append(" chars total)");
            }

            searchFrom = valueEnd;
        }

        return sb.ToString();
    }
}
