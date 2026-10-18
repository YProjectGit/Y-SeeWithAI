// VisionToSpeech.cs
// 3B.SpeechToSpeechLiveAPI の姉妹デモ。マイク PCM ではなく WebCam の JPEG フレームを Live API に送る。
// 左ペインにプレビュー・Stream トグル・吹き出し、中央に送信（Outbound）、右に受信（Inbound）。
//
// 上からの流れ:
//   Start → APIキー・systemInstruction・WebCam・AudioSource・Live 接続（Setup）
//   【シャッター（既定）】
//     Space → activityStart → JPEG 1枚 → 短いテキスト指示 → activityEnd → 音声返答
//   【ストリーミング】
//     Stream トグル ON → 約1 FPS で同様の送信を繰り返す（このあいだ Space 無効）
//     同じボタンで OFF → シャッターモードへ戻る
//   受信 → serverContent の音声を再生キューへ / output transcription を吹き出しへ

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
using UnityEngine.UI;

/// <summary>
/// Gemini Live API（WebSocket）で、カメラ画像から声の返答までを1セッションで可視化する。
/// </summary>
public class VisionToSpeech : MonoBehaviour
{
    // ===== インスペクタ: 設定 =====

    public string modelName = "gemini-3.1-flash-live-preview"; // Live モデル名（Setup の models/ 以下）
    public string apiKeyRelativePath = "Common/APIKey.txt"; // Assets/ からの相対パス
    public string systemInstructionRelativePath = "Common/SystemInstruction.txt"; // 事前指示ファイル
    public string voiceName = "Kore"; // Setup の prebuilt 声色名
    public int playbackSampleRate = 24000; // 受信 PCM の想定サンプルレート（Hz）
    public int maxSendLongSide = 768; // 送信 JPEG の長辺上限（推奨解像度）
    public int jpegQuality = 75; // EncodeToJPG の品質（1〜100）
    public float streamIntervalSeconds = 1f; // ストリーミング時の送信間隔（約1 FPS）
    public string mediaResolution = "MEDIA_RESOLUTION_MEDIUM"; // generationConfig の mediaResolution
    public string framePromptText =
        "この画像に写っているものを、日本語で短く説明してください。"; // フレームと一緒に送る指示

    // ===== インスペクタ: 左ペイン =====

    public TMP_InputField systemInstructionField; // systemInstruction。空なら Setup に載せない
    public TMP_Text recordHintText; // Space / Stream モードの案内
    public Button streamModeButton; // ストリーミングのトグル
    public RawImage webcamPreview; // WebCam プレビュー（未設定なら起動時に生成）
    public Transform messageContent; // バブルを並べる Content
    public ChatBubble messageBubblePrefab; // 1A と同型の吹き出し Prefab
    public ScrollRect chatScrollRect; // 新着時に下端へ

    // ===== インスペクタ: 可視化（段階バー / 送信 / 受信） =====

    public TMP_Text statusText; // 左下 Status
    public TMP_Text stageBarText; // Connect → Send Frame → Receive PCM → Play
    public TMP_Text setupHeaderText; // 送信ペイン上部: Setup 設定エッセンス
    public TMP_Text outboundLogText; // 送信ログ（追記）
    public TMP_Text outboundStatusText; // 送信中（1行）
    public TMP_Text inboundHeaderText; // 受信ペイン上部
    public TMP_Text inboundLogText; // 受信チャンクログ
    public TMP_Text transcriptionText; // output 文字起こしログ
    public TMP_Text inboundStatusText; // 受信中 / 再生中（1行）
    public AudioSource playbackAudioSource; // 受信音声の再生先

    // ===== 内部状態 =====

    string apiKey; // APIキー（画面・ログに全文は出さない）
    ClientWebSocket socket; // Live セッション用 WebSocket
    CancellationTokenSource receiveCts; // 受信ループ停止用
    readonly ConcurrentQueue<Action> mainThreadActions = new ConcurrentQueue<Action>(); // 受信→メイン
    readonly ConcurrentQueue<byte[]> playbackPcmQueue = new ConcurrentQueue<byte[]>(); // 再生待ち PCM
    readonly StringBuilder outboundLog = new StringBuilder(); // 送信ログ本文
    readonly StringBuilder inboundLog = new StringBuilder(); // 受信ログ本文
    readonly StringBuilder transcriptionLog = new StringBuilder(); // 文字起こしログ本文

    bool setupComplete; // setupComplete 受信済みか
    bool isConnected; // ソケットが Open か
    bool streamMode; // true=連続送信 / false=Space シャッター
    bool isTurnBusy; // 1ターン送信中（二重送信防止）
    bool playbackCoroutineRunning; // 再生コルーチン稼働中か
    WebCamTexture webCamTexture; // プレビュー兼キャプチャ源
    Coroutine streamLoopCoroutine; // ストリーミング用ループ
    long outboundTotalBytes; // 送信累計バイト
    long inboundTotalBytes; // 受信累計バイト
    int outboundFrameCount; // 送信フレーム数
    int inboundChunkCount; // 受信チャンク数
    string outputTranscriptBuffer; // 出力 transcription の蓄積
    DateTime systemInstructionFileWriteTimeUtc; // SystemInstruction.txt 同期用
    bool statusBlink; // 接続中 / 応答待ちのとき Status を点滅させる
    Stage currentStage = Stage.Connect; // 段階バー用
    TMP_Text streamModeButtonLabel; // ボタン上のラベル
    Image streamModeButtonImage; // ボタン色
    string lastSetupJsonForDisplay; // Setup ヘッダ用
    float replyWaitStarted = -1f; // 送信完了（activityEnd）時刻。未計測は -1

    const int MaxLogChars = 8000; // ログ欄の上限
    const float StatusBlinkSpeed = 6f; // 点滅の速さ（大きいほど速い）
    const int WebcamRequestWidth = 1280; // WebCam 要求解像度（プレビュー用）
    const int WebcamRequestHeight = 720;
    static readonly Color StreamButtonOffColor = new Color(0.25f, 0.55f, 0.9f, 1f);
    static readonly Color StreamButtonOnColor = new Color(0.2f, 0.65f, 0.35f, 1f);
    const string WsEndpoint =
        "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";

    enum Stage
    {
        Connect,
        SendFrame,
        ReceivePcm,
        Play
    }

    // ----- エントリポイント -----

    // 起動時: キー・UI・WebCam・再生・Live 接続を順に用意する
    void Start()
    {
        LoadApiKey();
        LoadSystemInstructionFromFile();
        if (systemInstructionField != null)
        {
            systemInstructionField.onEndEdit.AddListener(OnSystemInstructionEndEdit);
        }

        EnsurePlaybackAudioSource();
        SetupStreamModeButton();
        EnsureWebcamPreview();
        SetupWebcam();
        InitPanelTexts();
        RefreshModeUi();

        if (string.IsNullOrEmpty(apiKey))
        {
            SetStatus("準備エラー（APIキー）", false);
            SetStreamButtonInteractable(false);
            return;
        }

        if (webCamTexture == null)
        {
            SetStatus("準備エラー（カメラなし）", false);
            // 接続は試みるが、送信はできない
        }

        StartCoroutine(ConnectLiveSessionCoroutine());
        StartCoroutine(PlaybackPumpCoroutine());
    }

    // Space シャッターと、受信スレッドからの UI 更新をメインスレッドで処理する
    void Update()
    {
        DrainMainThreadActions();
        UpdateShutterInput();
        UpdateStatusBlink();
    }

    // 終了時にソケットとカメラを解放する
    void OnDestroy()
    {
        StopStreamingInternal();
        CloseSocket(true);
        StopWebcam();
    }

    // Stream トグルボタンのラベル／クリックを配線する
    void SetupStreamModeButton()
    {
        if (streamModeButton == null)
        {
            return;
        }

        streamModeButtonLabel = streamModeButton.GetComponentInChildren<TMP_Text>(true);
        streamModeButtonImage = streamModeButton.GetComponent<Image>();
        streamModeButton.onClick.RemoveListener(OnStreamModeButtonClicked);
        streamModeButton.onClick.AddListener(OnStreamModeButtonClicked);
    }

    // ----- 接続（Setup） -----

    // WebSocket を開き、最初の Setup JSON を送って setupComplete を待つ
    IEnumerator ConnectLiveSessionCoroutine()
    {
        SetStage(Stage.Connect);
        SetStatus("接続中…", true);
        SetOutboundStatus("—");
        SetInboundStatus("—");
        SetStreamButtonInteractable(false);

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
            SetStreamButtonInteractable(true);
            yield break;
        }

        string setupJson = BuildSetupJson();
        RefreshSetupHeader(setupJson);
        SetInboundHeaderStatic();

        Task sendSetup = SendTextAsync(setupJson);
        while (!sendSetup.IsCompleted)
        {
            yield return null;
        }

        if (sendSetup.IsFaulted)
        {
            ShowError("Setup 送信失敗: " + sendSetup.Exception.GetBaseException().Message);
            SetStreamButtonInteractable(true);
            yield break;
        }

        AppendOutboundLog("Setup 送信完了（設定は上部ヘッダ）");
        _ = Task.Run(() => ReceiveLoopAsync(receiveCts.Token));

        float wait = 0f;
        while (!setupComplete && wait < 15f)
        {
            wait += Time.unscaledDeltaTime;
            if (socket == null || socket.State != WebSocketState.Open)
            {
                SetStreamButtonInteractable(true);
                yield break;
            }

            yield return null;
        }

        if (!setupComplete)
        {
            ShowError("setupComplete がタイムアウトしました。モデル名とキーを確認してください。");
            SetStreamButtonInteractable(true);
            yield break;
        }

        isConnected = true;
        SetStage(Stage.Connect);
        SetStreamButtonInteractable(true);
        RefreshModeUi();
        SetStatus(GetReadyStatusText(), false);
        Debug.Log("[VisionToSpeech] Live セッション準備完了。stream=" + streamMode);
    }

    // Setup メッセージ（最初に1回だけ送るセッション設定）
    string BuildSetupJson()
    {
        StringBuilder sb = new StringBuilder(512);
        sb.Append("{\"setup\":{");
        sb.Append("\"model\":\"models/");
        sb.Append(GeminiJson.Escape(modelName));
        sb.Append("\",");

        // シャッター／ストリームともクライアントが activityStart/End でターンを区切る
        sb.Append("\"realtimeInputConfig\":{\"automaticActivityDetection\":{\"disabled\":true}},");
        sb.Append("\"outputAudioTranscription\":{},");

        string instruction = GetSystemInstructionText();
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

    // ----- Stream トグル -----

    // 同じボタンでストリーミング ON/OFF。Setup は変えないので再接続しない
    void OnStreamModeButtonClicked()
    {
        if (!isConnected || !setupComplete)
        {
            ShowError("まだ接続できていません。");
            return;
        }

        if (webCamTexture == null || !webCamTexture.isPlaying)
        {
            ShowError("カメラが使えません。");
            return;
        }

        streamMode = !streamMode;
        RefreshModeUi();

        if (streamMode)
        {
            AppendOutboundLog("stream ON（約 " + streamIntervalSeconds + "s 間隔）");
            streamLoopCoroutine = StartCoroutine(StreamLoopCoroutine());
            SetStatus("ストリーミング中（Space 無効）", true);
            SetOutboundStatus("連続送信");
        }
        else
        {
            StopStreamingInternal();
            AppendOutboundLog("stream OFF（シャッターへ）");
            SetOutboundStatus("—");
            SetStatus(GetReadyStatusText(), false);
            SetStage(Stage.Connect);
        }
    }

    // 約1 FPS でシャッターと同じ1ターン送信を繰り返す（前ターン未完了なら待つ）
    IEnumerator StreamLoopCoroutine()
    {
        // 直後に1回送り、以降は間隔待ち
        while (streamMode && isConnected)
        {
            if (!isTurnBusy)
            {
                yield return StartCoroutine(SendFrameTurnCoroutine(true));
            }

            float waited = 0f;
            while (streamMode && waited < streamIntervalSeconds)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }
        }

        streamLoopCoroutine = null;
    }

    void StopStreamingInternal()
    {
        streamMode = false;
        if (streamLoopCoroutine != null)
        {
            StopCoroutine(streamLoopCoroutine);
            streamLoopCoroutine = null;
        }
    }

    // 案内文・ボタン表示をモードに合わせる
    void RefreshModeUi()
    {
        if (recordHintText != null)
        {
            recordHintText.text = streamMode
                ? "ストリーミング中（Space 無効）"
                : "Space でシャッター（1フレーム送信）";
        }

        if (streamModeButtonLabel != null)
        {
            streamModeButtonLabel.text = streamMode ? "Stream ON" : "Stream";
        }

        if (streamModeButtonImage != null)
        {
            streamModeButtonImage.color = streamMode ? StreamButtonOnColor : StreamButtonOffColor;
        }

        if (setupHeaderText == null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(lastSetupJsonForDisplay) && isConnected)
        {
            setupHeaderText.text =
                BuildSetupSettingsSummary() + "\n\n"
                + GeminiJson.PrettyPrint(GeminiJson.Truncate(lastSetupJsonForDisplay, 400));
        }
        else
        {
            setupHeaderText.text =
                BuildSetupSettingsSummary() + "\n\n（接続後に Setup JSON を表示）";
        }
    }

    void SetStreamButtonInteractable(bool interactable)
    {
        if (streamModeButton != null)
        {
            streamModeButton.interactable = interactable;
        }
    }

    string GetReadyStatusText()
    {
        return streamMode
            ? "ストリーミング中（Space 無効）"
            : "接続済み（Space でシャッター）";
    }

    // ----- Space シャッター -----

    // ストリーム OFF・非ビジーのときだけ Space Down で1フレーム送信
    void UpdateShutterInput()
    {
        if (streamMode || isTurnBusy || !isConnected || !setupComplete)
        {
            return;
        }

        if (IsTypingInSystemInstruction())
        {
            return;
        }

        if (Input.GetKeyDown(KeyCode.Space))
        {
            StartCoroutine(SendFrameTurnCoroutine(false));
        }
    }

    bool IsTypingInSystemInstruction()
    {
        if (systemInstructionField != null && systemInstructionField.isFocused)
        {
            return true;
        }

        if (EventSystem.current == null || EventSystem.current.currentSelectedGameObject == null)
        {
            return false;
        }

        return systemInstructionField != null
               && EventSystem.current.currentSelectedGameObject == systemInstructionField.gameObject;
    }

    // 1ターン: activityStart → JPEG → テキスト指示 → activityEnd
    IEnumerator SendFrameTurnCoroutine(bool fromStream)
    {
        if (isTurnBusy)
        {
            yield break;
        }

        if (webCamTexture == null || !webCamTexture.isPlaying)
        {
            ShowError("カメラが準備できていません。");
            yield break;
        }

        if (socket == null || socket.State != WebSocketState.Open)
        {
            ShowError("未接続のため送信できません。");
            yield break;
        }

        isTurnBusy = true;
        SyncSystemInstructionBeforeSend();
        outputTranscriptBuffer = string.Empty;

        int width;
        int height;
        byte[] jpegBytes;
        if (!TryCaptureJpeg(out jpegBytes, out width, out height))
        {
            ShowError("フレームの JPEG 化に失敗しました。");
            isTurnBusy = false;
            yield break;
        }

        SetStage(Stage.SendFrame);
        SetStatus(fromStream ? "ストリーム送信中" : "シャッター送信中", true);
        SetOutboundStatus(fromStream ? "連続送信" : "送信中");
        SetInboundStatus("—");

        yield return StartCoroutine(SendJsonCoroutine("{\"realtimeInput\":{\"activityStart\":{}}}"));
        AppendOutboundLog("activityStart");

        string b64 = Convert.ToBase64String(jpegBytes);
        string videoJson =
            "{\"realtimeInput\":{\"video\":{\"mimeType\":\"image/jpeg\",\"data\":\""
            + b64
            + "\"}}}";
        yield return StartCoroutine(SendJsonCoroutine(videoJson));

        outboundFrameCount++;
        outboundTotalBytes += jpegBytes.Length;
        AppendOutboundLog(
            "+frame " + width + "x" + height + " " + jpegBytes.Length + "B / total "
            + outboundTotalBytes + "B  #" + outboundFrameCount);

        // 画像だけだと応答のきっかけが弱いことがあるので、短い指示テキストも同じターンで送る
        // 3.1 Live は会話中の clientContent を拒否するので realtimeInput.text を使う
        string prompt = string.IsNullOrEmpty(framePromptText)
            ? "この画像を日本語で短く説明してください。"
            : framePromptText;
        string textJson =
            "{\"realtimeInput\":{\"text\":\""
            + GeminiJson.Escape(prompt)
            + "\"}}";
        yield return StartCoroutine(SendJsonCoroutine(textJson));
        AppendOutboundLog("realtimeInput text（説明指示）");

        yield return StartCoroutine(SendJsonCoroutine("{\"realtimeInput\":{\"activityEnd\":{}}}"));
        AppendOutboundLog("activityEnd");
        replyWaitStarted = Time.realtimeSinceStartup; // 送信完了。返信までの計測用

        AddBubble("You", "（キャプチャ " + width + "x" + height + "）", true);

        SetOutboundStatus(fromStream ? "連続送信" : "—");
        SetInboundStatus("受信待ち");
        SetStatus("返答待ち", true);
        SetStage(Stage.ReceivePcm);
        // isTurnBusy は turnComplete で解除（ストリームは完了を待ってから次フレーム）
    }

    // WebCam の現フレームを長辺制限つき JPEG にする
    bool TryCaptureJpeg(out byte[] jpegBytes, out int width, out int height)
    {
        jpegBytes = null;
        width = 0;
        height = 0;

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

        width = sendTex.width;
        height = sendTex.height;
        jpegBytes = sendTex.EncodeToJPG(Mathf.Clamp(jpegQuality, 1, 100));
        Destroy(sendTex);
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
                            string closeReason = result.CloseStatusDescription;
                            EnqueueMain(() =>
                            {
                                isConnected = false;
                                if (!string.IsNullOrEmpty(closeReason))
                                {
                                    ShowError("切断: " + closeReason);
                                }
                                else
                                {
                                    SetStatus("切断されました", false);
                                }
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
            EnqueueMain(() =>
            {
                setupComplete = true;
                AppendOutboundLog("setupComplete 受信");
            });
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
            EnqueueMain(() =>
            {
                ClearPlaybackQueue();
                SetInboundStatus("割り込み");
            });
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
                if (pcm.Length < 2)
                {
                    continue;
                }

                playbackPcmQueue.Enqueue(pcm);
                int len = pcm.Length;
                EnqueueMain(() =>
                {
                    inboundChunkCount++;
                    inboundTotalBytes += len;
                    AppendInboundLog(
                        "+audio " + len + "B / total " + inboundTotalBytes + "B  " + playbackSampleRate + "Hz");
                    SetStage(Stage.ReceivePcm);
                    SetInboundStatus("受信中");
                });
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
        AppendTranscriptionLog("out: " + fragment);
        SetStage(Stage.ReceivePcm);
    }

    // ターン完了: 吹き出し確定、ビジー解除、再生段階へ
    void OnTurnComplete()
    {
        if (replyWaitStarted >= 0f)
        {
            ResponseTime.Log("合計", replyWaitStarted);
            replyWaitStarted = -1f;
        }

        string modelText = (outputTranscriptBuffer ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(modelText))
        {
            AddBubble("Gemini", modelText, false);
        }

        outputTranscriptBuffer = string.Empty;
        isTurnBusy = false;
        SetInboundStatus(playbackPcmQueue.IsEmpty ? "—" : "再生中");
        if (!playbackPcmQueue.IsEmpty)
        {
            SetStage(Stage.Play);
        }

        if (streamMode)
        {
            SetStatus("ストリーミング中（Space 無効）", true);
            SetOutboundStatus("連続送信");
            SetStage(Stage.SendFrame);
        }
        else
        {
            SetStatus(GetReadyStatusText(), false);
            SetOutboundStatus("—");
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

            SetStage(Stage.Play);
            SetInboundStatus("再生中");
            playbackAudioSource.clip = clip;
            playbackAudioSource.Play();
            while (playbackAudioSource != null && playbackAudioSource.isPlaying)
            {
                yield return null;
            }

            Destroy(clip);
            if (playbackPcmQueue.IsEmpty)
            {
                SetInboundStatus("—");
                if (!streamMode && !isTurnBusy)
                {
                    SetStage(Stage.Connect);
                }
                else if (streamMode)
                {
                    SetStage(Stage.SendFrame);
                }
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

    // ----- UI / ログ -----

    void InitPanelTexts()
    {
        SetStage(Stage.Connect);
        if (outboundLogText != null)
        {
            outboundLogText.text = "（まだ送信していません）";
        }

        if (inboundLogText != null)
        {
            inboundLogText.text = "（まだ受信していません）";
        }

        if (transcriptionText != null)
        {
            transcriptionText.text = "（transcription 待ち）";
        }

        SetOutboundStatus("—");
        SetInboundStatus("—");
        SetInboundHeaderStatic();
        if (setupHeaderText != null)
        {
            setupHeaderText.text =
                BuildSetupSettingsSummary() + "\n\n（接続後に Setup JSON を表示）";
        }
    }

    string BuildSetupSettingsSummary()
    {
        string modeLine = streamMode
            ? "mode: stream (~" + streamIntervalSeconds + "s)\n"
            : "mode: shutter (Space → 1 frame)\n";

        return "model: " + modelName + "\n"
               + "responseModalities: AUDIO\n"
               + "voice: " + voiceName + "\n"
               + "transcription: output ON\n"
               + "mediaResolution: " + mediaResolution + "\n"
               + "send: JPEG ≤" + maxSendLongSide + "\n"
               + modeLine
               + "key: " + GeminiKey.Mask(apiKey);
    }

    void RefreshSetupHeader(string setupJson)
    {
        lastSetupJsonForDisplay = setupJson;
        if (setupHeaderText == null)
        {
            return;
        }

        setupHeaderText.text =
            BuildSetupSettingsSummary() + "\n\n"
            + GeminiJson.PrettyPrint(GeminiJson.Truncate(setupJson, 400));
    }

    void SetInboundHeaderStatic()
    {
        if (inboundHeaderText == null)
        {
            return;
        }

        inboundHeaderText.text =
            "output: PCM " + playbackSampleRate + "Hz\n"
            + "mime: audio/pcm (L16 LE)\n"
            + "channels: 1\n"
            + "transcription: serverContent 経由";
    }

    void SetStage(Stage stage)
    {
        currentStage = stage;
        if (stageBarText == null)
        {
            return;
        }

        string c = stage == Stage.Connect ? "[Connect]" : "Connect";
        string s = stage == Stage.SendFrame ? "[Send Frame]" : "Send Frame";
        string r = stage == Stage.ReceivePcm ? "[Receive PCM]" : "Receive PCM";
        string p = stage == Stage.Play ? "[Play]" : "Play";
        stageBarText.text = c + " → " + s + " → " + r + " → " + p;
    }

    void AppendOutboundLog(string line)
    {
        AppendLog(outboundLog, outboundLogText, line);
    }

    void AppendInboundLog(string line)
    {
        AppendLog(inboundLog, inboundLogText, line);
    }

    void AppendTranscriptionLog(string line)
    {
        AppendLog(transcriptionLog, transcriptionText, line);
    }

    static void AppendLog(StringBuilder sb, TMP_Text view, string line)
    {
        if (sb.Length > 0)
        {
            sb.Append('\n');
        }

        sb.Append(line);
        if (sb.Length > MaxLogChars)
        {
            sb.Remove(0, sb.Length - MaxLogChars);
        }

        if (view != null)
        {
            view.text = sb.ToString();
        }
    }

    void SetOutboundStatus(string text)
    {
        if (outboundStatusText != null)
        {
            outboundStatusText.text = text;
        }
    }

    void SetInboundStatus(string text)
    {
        if (inboundStatusText != null)
        {
            inboundStatusText.text = text;
        }
    }

    // Status 欄を日本語で更新する。blink=true のとき点滅（接続中 / 応答待ち用）
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

    // 接続中・応答待ちのあいだだけアルファを上下させて点滅させる
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
        Debug.LogError("[VisionToSpeech] " + message);
        SetStatus("エラー", false);
        isTurnBusy = false;
        AddBubble("Error", message, false);
        AppendInboundLog("[Error] " + message);
    }

    void AddBubble(string speaker, string body, bool isUser)
    {
        if (messageBubblePrefab == null || messageContent == null)
        {
            return;
        }

        ChatBubble bubble = Instantiate(messageBubblePrefab, messageContent);
        bubble.SetMessage(speaker, body, isUser);
        Canvas.ForceUpdateCanvases();
        if (chatScrollRect != null)
        {
            chatScrollRect.verticalNormalizedPosition = 0f;
        }
    }

    // ----- WebCam / 再生 -----

    // プレビュー RawImage が未配線なら、案内テキストの近くに生成する
    void EnsureWebcamPreview()
    {
        if (webcamPreview != null)
        {
            return;
        }

        if (recordHintText == null)
        {
            return;
        }

        RectTransform hintRt = recordHintText.rectTransform;
        Transform parent = hintRt.parent;
        GameObject go = new GameObject("WebcamPreview", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
        go.transform.SetParent(parent, false);
        go.transform.SetSiblingIndex(Mathf.Max(0, hintRt.GetSiblingIndex()));

        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.sizeDelta = new Vector2(0f, 160f);
        rt.anchoredPosition = new Vector2(0f, -8f);

        webcamPreview = go.GetComponent<RawImage>();
        webcamPreview.color = Color.white;
    }

    void SetupWebcam()
    {
        if (WebCamTexture.devices == null || WebCamTexture.devices.Length == 0)
        {
            webCamTexture = null;
            Debug.LogWarning("[VisionToSpeech] カメラがありません。");
            if (webcamPreview != null)
            {
                webcamPreview.color = new Color(0.2f, 0.2f, 0.2f, 1f);
            }

            return;
        }

        string deviceName = WebCamTexture.devices[0].name;
        webCamTexture = new WebCamTexture(deviceName, WebcamRequestWidth, WebcamRequestHeight, 30);
        webCamTexture.Play();
        if (webcamPreview != null)
        {
            webcamPreview.texture = webCamTexture;
        }

        Debug.Log("[VisionToSpeech] カメラ: " + deviceName);
    }

    void StopWebcam()
    {
        if (webCamTexture != null)
        {
            if (webCamTexture.isPlaying)
            {
                webCamTexture.Stop();
            }

            Destroy(webCamTexture);
            webCamTexture = null;
        }
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

    // ----- systemInstruction / APIキー -----

    void LoadApiKey()
    {
        string error;
        if (!GeminiKey.TryRead(apiKeyRelativePath, out apiKey, out error))
        {
            Debug.LogError("[VisionToSpeech] " + error);
            return;
        }
    }

    string GetSystemInstructionFilePath()
    {
        return Path.Combine(Application.dataPath, systemInstructionRelativePath);
    }

    void LoadSystemInstructionFromFile()
    {
        if (systemInstructionField == null)
        {
            return;
        }

        string path = GetSystemInstructionFilePath();
        if (!File.Exists(path))
        {
            systemInstructionField.text = string.Empty;
            return;
        }

        systemInstructionField.text = File.ReadAllText(path);
        systemInstructionFileWriteTimeUtc = File.GetLastWriteTimeUtc(path);
    }

    // InputField の編集確定時
    void OnSystemInstructionEndEdit(string _)
    {
        SaveSystemInstructionFromField();
    }

    void SaveSystemInstructionFromField()
    {
        if (systemInstructionField == null)
        {
            return;
        }

        string path = GetSystemInstructionFilePath();
        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(path, systemInstructionField.text ?? string.Empty, new UTF8Encoding(false));
        systemInstructionFileWriteTimeUtc = File.GetLastWriteTimeUtc(path);
    }

    void SyncSystemInstructionBeforeSend()
    {
        if (systemInstructionField == null)
        {
            return;
        }

        string path = GetSystemInstructionFilePath();
        if (File.Exists(path))
        {
            DateTime write = File.GetLastWriteTimeUtc(path);
            if (write > systemInstructionFileWriteTimeUtc && !systemInstructionField.isFocused)
            {
                systemInstructionField.text = File.ReadAllText(path);
                systemInstructionFileWriteTimeUtc = write;
            }
        }

        SaveSystemInstructionFromField();
    }

    string GetSystemInstructionText()
    {
        if (systemInstructionField == null || systemInstructionField.text == null)
        {
            return string.Empty;
        }

        return systemInstructionField.text.Trim();
    }

    // ----- メインスレッド橋渡し / 切断 -----

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
                Debug.LogError("[VisionToSpeech] メインスレッド処理例外: " + e.Message);
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
