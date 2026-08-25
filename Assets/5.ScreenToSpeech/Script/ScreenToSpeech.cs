// ScreenToSpeech.cs
// 4.VisionToSpeech の姉妹デモ。WebCam ではなく、描画パッドの JPEG を Live API に送る。
// 送信／受信のテキスト欄は出さず、紙・消す・状態・字幕だけの体験画面にする。
//
// 上からの流れ:
//   Start → APIキー・体験 UI・AudioSource・Live 接続（Setup）
//   描く → dirty なら約1秒間隔で
//     activityStart → JPEG → 短い実況指示 → activityEnd → 音声返答
//   受信 → serverContent の音声を再生キューへ / output transcription を字幕へ
//   消す → 紙を白紙に（空の紙は送らない）

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

/// <summary>
/// Gemini Live API（WebSocket）で、描いている絵への解釈を声で返す。
/// </summary>
public class ScreenToSpeech : MonoBehaviour
{
    // ===== インスペクタ: 設定 =====

    public string modelName = "gemini-3.1-flash-live-preview"; // Live モデル名（Setup の models/ 以下）
    public string apiKeyRelativePath = "Common/APIKey.txt"; // Assets/ からの相対パス
    public string voiceName = "Kore"; // Setup の prebuilt 声色名
    public int playbackSampleRate = 24000; // 受信 PCM の想定サンプルレート（Hz）
    public int maxSendLongSide = 768; // 送信 JPEG の長辺上限（推奨解像度）
    public int jpegQuality = 75; // EncodeToJPG の品質（1〜100）
    public float interpretIntervalSeconds = 1f; // 自動送信の間隔（約1 FPS）
    public string mediaResolution = "MEDIA_RESOLUTION_MEDIUM"; // generationConfig の mediaResolution
    public string systemInstructionText =
        "あなたは、人がいま描いている絵を見る実況者です。紙に見えている線や形を、日本語で短くやさしく話してください。途中の線でも、いま見えているものから推測してよいです。まだほとんど描かれていないときは、無理に物語を作らず、見えていることだけを言ってください。返答は1〜2文にしてください。"; // Setup の事前指示（画面には出さない）
    public string framePromptText =
        "このキャンバスに描かれている絵を、日本語で短く実況してください。"; // フレームと一緒に送る指示

    // ===== インスペクタ: 体験 UI =====

    public DrawingPad drawingPad; // 描画キャンバス
    public Button clearButton; // 紙を消す
    public TMP_Text statusText; // 接続中 / 描いてください / 見てます / 話しています
    public TMP_Text captionText; // いまの声の内容（字幕）
    public AudioSource playbackAudioSource; // 受信音声の再生先

    // ===== 内部状態 =====

    string apiKey; // APIキー（画面には出さない）
    ClientWebSocket socket; // Live セッション用 WebSocket
    CancellationTokenSource receiveCts; // 受信ループ停止用
    readonly ConcurrentQueue<Action> mainThreadActions = new ConcurrentQueue<Action>(); // 受信→メイン
    readonly ConcurrentQueue<byte[]> playbackPcmQueue = new ConcurrentQueue<byte[]>(); // 再生待ち PCM

    bool setupComplete; // setupComplete 受信済みか
    bool isConnected; // ソケットが Open か
    bool isTurnBusy; // 1ターン送信中（二重送信防止）
    bool playbackCoroutineRunning; // 再生コルーチン稼働中か
    string outputTranscriptBuffer; // 出力 transcription の蓄積
    float replyWaitStarted = -1f; // 送信完了（activityEnd）時刻。未計測は -1

    const string WsEndpoint =
        "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";

    static readonly Color BackgroundColor = new Color(0.12f, 0.11f, 0.10f, 1f); // カメラ背景

    // ----- エントリポイント -----

    // 起動時: キー・体験 UI・再生・Live 接続を順に用意する
    void Start()
    {
        LoadApiKey();
        EnsureRuntimeBasics();
        EnsurePlaybackAudioSource();
        WireClearButton();
        SetCaption(string.Empty);

        if (string.IsNullOrEmpty(apiKey))
        {
            SetStatus("準備エラー（APIキー）");
            return;
        }

        StartCoroutine(ConnectLiveSessionCoroutine());
        StartCoroutine(PlaybackPumpCoroutine());
    }

    // 受信スレッドからの UI 更新をメインスレッドで処理する
    void Update()
    {
        DrainMainThreadActions();
    }

    // 終了時にソケットを解放する
    void OnDestroy()
    {
        CloseSocket(true);
    }

    // ----- 接続（Setup） -----

    // WebSocket を開き、最初の Setup JSON を送って setupComplete を待つ
    IEnumerator ConnectLiveSessionCoroutine()
    {
        SetStatus("接続中…");

        Uri uri = new Uri(WsEndpoint + "?key=" + Uri.EscapeDataString(apiKey));
        socket = new ClientWebSocket();
        receiveCts = new CancellationTokenSource();

        Task connectTask = socket.ConnectAsync(uri, receiveCts.Token);
        while (!connectTask.IsCompleted)
        {
            yield return null;
        }

        if (connectTask.IsFaulted || socket.State != WebSocketState.Open)
        {
            string err = connectTask.Exception != null
                ? connectTask.Exception.GetBaseException().Message
                : "WebSocket を Open にできませんでした";
            ShowError("Live 接続失敗: " + err);
            yield break;
        }

        Task sendSetup = SendTextAsync(BuildSetupJson());
        while (!sendSetup.IsCompleted)
        {
            yield return null;
        }

        if (sendSetup.IsFaulted)
        {
            ShowError("Setup 送信失敗: " + sendSetup.Exception.GetBaseException().Message);
            yield break;
        }

        _ = Task.Run(() => ReceiveLoopAsync(receiveCts.Token));

        float wait = 0f;
        while (!setupComplete && wait < 15f)
        {
            wait += Time.unscaledDeltaTime;
            yield return null;
        }

        if (!setupComplete)
        {
            ShowError("setupComplete がタイムアウトしました。モデル名とキーを確認してください。");
            yield break;
        }

        isConnected = true;
        SetStatus("描いてください");
        Debug.Log("[ScreenToSpeech] Live セッション準備完了。");
        StartCoroutine(InterpretLoopCoroutine());
    }

    // Setup メッセージ（最初に1回だけ送るセッション設定）
    string BuildSetupJson()
    {
        StringBuilder sb = new StringBuilder(512);
        sb.Append("{\"setup\":{");
        sb.Append("\"model\":\"models/");
        sb.Append(GeminiJson.Escape(modelName));
        sb.Append("\",");

        // クライアントが activityStart/End でターンを区切る（4 と同じ）
        sb.Append("\"realtimeInputConfig\":{\"automaticActivityDetection\":{\"disabled\":true}},");
        sb.Append("\"outputAudioTranscription\":{},");

        string instruction = systemInstructionText != null ? systemInstructionText.Trim() : string.Empty;
        if (!string.IsNullOrEmpty(instruction))
        {
            sb.Append("\"systemInstruction\":{\"parts\":[{\"text\":\"");
            sb.Append(GeminiJson.Escape(instruction));
            sb.Append("\"}]},");
        }

        sb.Append("\"generationConfig\":{");
        sb.Append("\"responseModalities\":[\"AUDIO\"],");
        if (!string.IsNullOrEmpty(mediaResolution))
        {
            // GenerationConfig のフィールド。setup 直下に置くとサーバが切断する
            sb.Append("\"mediaResolution\":\"");
            sb.Append(GeminiJson.Escape(mediaResolution));
            sb.Append("\",");
        }

        sb.Append("\"speechConfig\":{\"voiceConfig\":{\"prebuiltVoiceConfig\":{\"voiceName\":\"");
        sb.Append(GeminiJson.Escape(voiceName));
        sb.Append("\"}}}");
        sb.Append('}'); // generationConfig
        sb.Append("}}"); // setup + root
        return sb.ToString();
    }

    // ----- 自動送信（描きながらの解釈） -----

    // dirty かつインクありなら、約1秒間隔で1ターン送る
    IEnumerator InterpretLoopCoroutine()
    {
        while (isConnected)
        {
            float waited = 0f;
            while (isConnected && waited < interpretIntervalSeconds)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            if (!isConnected || !setupComplete || isTurnBusy)
            {
                continue;
            }

            if (drawingPad == null || !drawingPad.HasInk || !drawingPad.IsDirty)
            {
                continue;
            }

            yield return StartCoroutine(SendFrameTurnCoroutine());
        }
    }

    // 1ターン: activityStart → JPEG → テキスト指示 → activityEnd
    IEnumerator SendFrameTurnCoroutine()
    {
        if (isTurnBusy)
        {
            yield break;
        }

        if (socket == null || socket.State != WebSocketState.Open)
        {
            ShowError("未接続のため送信できません。");
            yield break;
        }

        int width;
        int height;
        byte[] jpegBytes;
        if (!TryCaptureJpeg(out jpegBytes, out width, out height))
        {
            ShowError("フレームの JPEG 化に失敗しました。");
            yield break;
        }

        isTurnBusy = true;
        drawingPad.MarkClean();
        outputTranscriptBuffer = string.Empty;
        ClearPlaybackQueue();
        SetStatus("見てます…");

        yield return StartCoroutine(SendJsonCoroutine("{\"realtimeInput\":{\"activityStart\":{}}}"));

        string b64 = Convert.ToBase64String(jpegBytes);
        string videoJson =
            "{\"realtimeInput\":{\"video\":{\"mimeType\":\"image/jpeg\",\"data\":\""
            + b64
            + "\"}}}";
        yield return StartCoroutine(SendJsonCoroutine(videoJson));

        // 画像だけだと応答のきっかけが弱いことがあるので、短い指示テキストも同じターンで送る
        // 3.1 Live は会話中の clientContent を拒否するので realtimeInput.text を使う
        string prompt = string.IsNullOrEmpty(framePromptText)
            ? "この絵を日本語で短く実況してください。"
            : framePromptText;
        string textJson =
            "{\"realtimeInput\":{\"text\":\""
            + GeminiJson.Escape(prompt)
            + "\"}}";
        yield return StartCoroutine(SendJsonCoroutine(textJson));

        yield return StartCoroutine(SendJsonCoroutine("{\"realtimeInput\":{\"activityEnd\":{}}}"));
        replyWaitStarted = Time.realtimeSinceStartup; // 送信完了。返信までの計測用
        Debug.Log("[ScreenToSpeech] 送信 " + width + "x" + height + " " + jpegBytes.Length + "B");
    }

    // キャンバスを長辺制限つき JPEG にする
    bool TryCaptureJpeg(out byte[] jpegBytes, out int width, out int height)
    {
        jpegBytes = null;
        width = 0;
        height = 0;

        if (drawingPad == null || drawingPad.CanvasTexture == null)
        {
            return false;
        }

        Texture2D src = drawingPad.CanvasTexture;
        int srcW = src.width;
        int srcH = src.height;
        if (srcW < 16 || srcH < 16)
        {
            return false;
        }

        int longSide = Mathf.Max(srcW, srcH);
        Texture2D sendTex = src;
        bool scaled = false;
        if (longSide > maxSendLongSide && maxSendLongSide > 0)
        {
            float scale = (float)maxSendLongSide / longSide;
            int dstW = Mathf.Max(1, Mathf.RoundToInt(srcW * scale));
            int dstH = Mathf.Max(1, Mathf.RoundToInt(srcH * scale));
            sendTex = TextureUtil.Scale(src, dstW, dstH);
            scaled = true;
        }

        width = sendTex.width;
        height = sendTex.height;
        jpegBytes = sendTex.EncodeToJPG(Mathf.Clamp(jpegQuality, 1, 100));
        if (scaled)
        {
            Destroy(sendTex);
        }

        return jpegBytes != null && jpegBytes.Length > 0;
    }

    // ----- 送受信 -----

    IEnumerator SendJsonCoroutine(string json)
    {
        if (socket == null || socket.State != WebSocketState.Open)
        {
            yield break;
        }

        Task task = SendTextAsync(json);
        while (!task.IsCompleted)
        {
            yield return null;
        }

        if (task.IsFaulted)
        {
            EnqueueMain(() => ShowError("送信エラー: " + task.Exception.GetBaseException().Message));
        }
    }

    async Task SendTextAsync(string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            true,
            receiveCts != null ? receiveCts.Token : CancellationToken.None).ConfigureAwait(false);
    }

    async Task ReceiveLoopAsync(CancellationToken token)
    {
        byte[] buffer = new byte[64 * 1024];
        try
        {
            while (socket != null && socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token)
                            .ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            EnqueueMain(() =>
                            {
                                isConnected = false;
                                SetStatus("切断されました");
                            });
                            return;
                        }

                        ms.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    string json = Encoding.UTF8.GetString(ms.ToArray());
                    HandleServerMessage(json);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (Exception e)
        {
            EnqueueMain(() => ShowError("受信ループ例外: " + e.Message));
        }
    }

    // サーバ JSON を種別ごとに振り分ける（教材用の最小パーサ）
    void HandleServerMessage(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return;
        }

        if (json.IndexOf("\"setupComplete\"", StringComparison.Ordinal) >= 0)
        {
            EnqueueMain(() => { setupComplete = true; });
            return;
        }

        TryExtractAndEnqueueAudio(json);

        if (json.IndexOf("\"outputTranscription\"", StringComparison.Ordinal) >= 0)
        {
            string t = GeminiJsonScan.NestedTextAfterKey(json, "outputTranscription");
            if (!string.IsNullOrEmpty(t))
            {
                EnqueueMain(() => OnOutputTranscription(t));
            }
        }

        if (json.IndexOf("\"turnComplete\":true", StringComparison.Ordinal) >= 0
            || json.IndexOf("\"turnComplete\": true", StringComparison.Ordinal) >= 0)
        {
            EnqueueMain(OnTurnComplete);
        }

        if (json.IndexOf("\"interrupted\":true", StringComparison.Ordinal) >= 0)
        {
            EnqueueMain(ClearPlaybackQueue);
        }
    }

    void TryExtractAndEnqueueAudio(string json)
    {
        const string marker = "\"data\":\"";
        if (json.IndexOf("\"inlineData\"", StringComparison.Ordinal) < 0
            && json.IndexOf("\"inline_data\"", StringComparison.Ordinal) < 0)
        {
            return;
        }

        if (json.IndexOf("audio", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return;
        }

        int searchFrom = 0;
        while (true)
        {
            int dataIndex = json.IndexOf(marker, searchFrom, StringComparison.Ordinal);
            if (dataIndex < 0)
            {
                break;
            }

            int valueStart = dataIndex + marker.Length;
            int valueEnd = json.IndexOf('"', valueStart);
            if (valueEnd < 0)
            {
                break;
            }

            string b64 = json.Substring(valueStart, valueEnd - valueStart);
            searchFrom = valueEnd + 1;
            if (b64.Length < 64)
            {
                continue;
            }

            try
            {
                byte[] pcm = Convert.FromBase64String(b64);
                if (pcm.Length >= 2)
                {
                    playbackPcmQueue.Enqueue(pcm);
                }
            }
            catch (FormatException)
            {
                // 非 Base64 は無視
            }
        }
    }

    void OnOutputTranscription(string fragment)
    {
        outputTranscriptBuffer = (outputTranscriptBuffer ?? string.Empty) + fragment;
        SetCaption(outputTranscriptBuffer.Trim());
        SetStatus("話しています");
    }

    // ターン完了: ビジー解除。字幕は残す
    void OnTurnComplete()
    {
        if (replyWaitStarted >= 0f)
        {
            ResponseTime.Log("合計", replyWaitStarted);
            replyWaitStarted = -1f;
        }

        isTurnBusy = false;
        if (playbackPcmQueue.IsEmpty && (playbackAudioSource == null || !playbackAudioSource.isPlaying))
        {
            SetStatus("描いてください");
        }
    }

    // ----- 再生 -----

    IEnumerator PlaybackPumpCoroutine()
    {
        playbackCoroutineRunning = true;
        while (playbackCoroutineRunning)
        {
            byte[] pcm;
            if (!playbackPcmQueue.TryDequeue(out pcm))
            {
                yield return null;
                continue;
            }

            EnsurePlaybackAudioSource();
            AudioClip clip = AudioCodec.Pcm16ToClip(pcm, playbackSampleRate);
            if (clip == null)
            {
                continue;
            }

            SetStatus("話しています");
            playbackAudioSource.clip = clip;
            playbackAudioSource.Play();
            while (playbackAudioSource != null && playbackAudioSource.isPlaying)
            {
                yield return null;
            }

            Destroy(clip);
            if (playbackPcmQueue.IsEmpty && !isTurnBusy)
            {
                SetStatus("描いてください");
            }
        }
    }

    void ClearPlaybackQueue()
    {
        byte[] ignored;
        while (playbackPcmQueue.TryDequeue(out ignored))
        {
        }

        if (playbackAudioSource != null && playbackAudioSource.isPlaying)
        {
            playbackAudioSource.Stop();
        }
    }

    // ----- 消す / 状態 / 字幕 -----

    void WireClearButton()
    {
        if (clearButton == null)
        {
            return;
        }

        clearButton.onClick.RemoveListener(OnClearClicked);
        clearButton.onClick.AddListener(OnClearClicked);
    }

    // 紙を白紙に戻し、字幕も消す。空の紙は送らない
    void OnClearClicked()
    {
        if (drawingPad != null)
        {
            drawingPad.Clear();
        }

        SetCaption(string.Empty);
        if (isConnected)
        {
            SetStatus("描いてください");
        }
    }

    void SetStatus(string text)
    {
        if (statusText != null)
        {
            statusText.text = text;
        }
    }

    void SetCaption(string text)
    {
        if (captionText != null)
        {
            captionText.text = text ?? string.Empty;
        }
    }

    void ShowError(string message)
    {
        Debug.LogError("[ScreenToSpeech] " + message);
        SetStatus("エラー");
        isTurnBusy = false;
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

    void EnsurePlaybackAudioSource()
    {
        if (playbackAudioSource != null)
        {
            return;
        }

        playbackAudioSource = GetComponent<AudioSource>();
        if (playbackAudioSource == null)
        {
            playbackAudioSource = gameObject.AddComponent<AudioSource>();
        }

        playbackAudioSource.playOnAwake = false;
    }

    // ----- APIキー / メインスレッド橋渡し / 切断 -----

    void LoadApiKey()
    {
        string error;
        if (!GeminiKey.TryRead(apiKeyRelativePath, out apiKey, out error))
        {
            Debug.LogError("[ScreenToSpeech] " + error);
            return;
        }
    }

    void EnqueueMain(Action action)
    {
        if (action != null)
        {
            mainThreadActions.Enqueue(action);
        }
    }

    void DrainMainThreadActions()
    {
        Action action;
        while (mainThreadActions.TryDequeue(out action))
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Debug.LogError("[ScreenToSpeech] メインスレッド処理例外: " + e.Message);
            }
        }
    }

    void CloseSocket(bool stopPlaybackPump)
    {
        if (stopPlaybackPump)
        {
            playbackCoroutineRunning = false;
        }

        try
        {
            if (receiveCts != null)
            {
                receiveCts.Cancel();
                receiveCts.Dispose();
            }
        }
        catch
        {
            // ignore
        }

        receiveCts = null;

        try
        {
            if (socket != null)
            {
                if (socket.State == WebSocketState.Open)
                {
                    socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                        .Wait(500);
                }

                socket.Dispose();
            }
        }
        catch
        {
            // ignore
        }

        socket = null;
        isConnected = false;
        setupComplete = false;
    }
}
