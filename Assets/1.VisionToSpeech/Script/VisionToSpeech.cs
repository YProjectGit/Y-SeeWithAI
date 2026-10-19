// VisionToSpeech.cs
// WebCam の JPEG フレームを Live API に送り、音声と字幕で返答を表示する。
// 上段: カメラ / 送信JPEG / 返答。下段: 設定 / 送信ログ / 受信ログ。
//
// 上からの流れ:
//   Start → APIキー・systemInstruction・WebCam・AudioSource・Live 接続（Setup）
//   【シャッター（既定）】
//     Space → 画像と指示を clientContent の1ターンにまとめて送信 → 音声返答
//   【ストリーミング】
//     Stream トグル ON → 約1 FPS で同様の送信を繰り返す（このあいだ Space 無効）
//     同じボタンで OFF → シャッターモードへ戻る
//   受信 → serverContent の音声を再生キューへ / output transcription を字幕へ

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
public partial class VisionToSpeech : MonoBehaviour
{
    // ===== インスペクタ: 設定 =====

    public string modelName = "gemini-3.1-flash-live-preview"; // Live モデル名（Setup の models/ 以下）
    public string apiKeyRelativePath = "Common/APIKey.txt"; // Assets/ からの相対パス
    public string systemInstructionRelativePath = "Common/SystemInstruction.txt"; // 事前指示ファイル
    public string voiceName = "Kore"; // Setup の prebuilt 声色名
    [Range(0.05f, 0.5f)] public float playbackBufferSeconds = 0.15f; // 通信の揺れを吸収する先読み秒数
    public int playbackSampleRate = 24000; // 受信 PCM の想定サンプルレート（Hz）
    public int maxSendLongSide = 768; // 送信 JPEG の長辺上限（推奨解像度）
    public int jpegQuality = 75; // EncodeToJPG の品質（1〜100）
    public float streamIntervalSeconds = 1f; // ストリーミング時の送信間隔（約1 FPS）
    public string mediaResolution = "MEDIA_RESOLUTION_MEDIUM"; // generationConfig の mediaResolution
    public string framePromptText =
        "この画像に写っているものを、日本語で短く説明してください。"; // フレームと一緒に送る指示

    // ===== インスペクタ: カメラ・設定 =====

    public TMP_InputField systemInstructionField; // systemInstruction。空なら Setup に載せない
    public TMP_Text recordHintText; // Space / Stream モードの案内
    public Button streamModeButton; // ストリーミングのトグル
    public RawImage webcamPreview; // WebCam プレビュー（未設定なら起動時に生成）

    // ===== インスペクタ: 設定 / 送信 / 受信 =====

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

    bool setupComplete; // setupComplete 受信済みか
    bool isConnected; // ソケットが Open か
    bool streamMode; // true=連続送信 / false=Space シャッター
    readonly GeminiResponseTurn response = new GeminiResponseTurn();
    bool isTurnBusy => response.IsBusy;
    PcmStreamPlayer pcmPlayer;
    bool playbackCoroutineRunning; // 再生コルーチン稼働中か
    WebCamTexture webCamTexture; // プレビュー兼キャプチャ源
    Coroutine streamLoopCoroutine; // ストリーミング用ループ
    long outboundTotalBytes; // 送信累計バイト
    int outboundFrameCount; // 送信フレーム数
    DateTime systemInstructionFileWriteTimeUtc; // SystemInstruction.txt 同期用
    TMP_Text streamModeButtonLabel; // ボタン上のラベル
    Image streamModeButtonImage; // ボタン色
    float replyWaitStarted = -1f; // 送信完了（turnComplete）時刻。未計測は -1

    const int WebcamRequestWidth = 1280; // WebCam 要求解像度（プレビュー用）
    const int WebcamRequestHeight = 720;
    const string WsEndpoint =
        "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";

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
        pcmPlayer = playbackAudioSource.GetComponent<PcmStreamPlayer>();
        if (pcmPlayer == null)
            pcmPlayer = playbackAudioSource.gameObject.AddComponent<PcmStreamPlayer>();
        pcmPlayer.Initialize(playbackSampleRate, playbackBufferSeconds);
        SetupStreamModeButton();
        EnsureWebcamPreview();
        SetupWebcam();
        InitPanelTexts();

        InitializeDashboard();

        if (string.IsNullOrEmpty(apiKey))
        {
            return;
        }

        StartCoroutine(ConnectLiveSessionCoroutine());
        StartCoroutine(PlaybackPumpCoroutine());
    }

    // Space シャッターと、受信スレッドからの UI 更新をメインスレッドで処理する
    void Update()
    {
        DrainMainThreadActions();
        UpdateShutterInput();
        UpdateDashboard();
    }

    // 終了時にソケットとカメラを解放する
    void OnDestroy()
    {
        StopStreamingInternal();
        CloseSocket(true);
        StopWebcam();
        if (sentTexture != null)
            Destroy(sentTexture);
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

        string setupJson = BuildSetupJson();

        SetInboundHeaderStatic();

        Task sendSetup = SendTextAsync(setupJson);
        while (!sendSetup.IsCompleted)
        {
            yield return null;
        }

        if (sendSetup.IsFaulted)
        {
            ShowError("Setup 送信失敗: " + sendSetup.Exception.GetBaseException().Message);

            yield break;
        }

        AppendOutboundLog("setup 送信完了 / AUDIO / outputAudioTranscription");
        _ = Task.Run(() => ReceiveLoopAsync(receiveCts.Token));

        float wait = 0f;
        while (!setupComplete && wait < 15f)
        {
            wait += Time.unscaledDeltaTime;
            if (socket == null || socket.State != WebSocketState.Open)
            {
                yield break;
            }

            yield return null;
        }

        if (!setupComplete)
        {
            ShowError("setupComplete がタイムアウトしました。モデル名とキーを確認してください。");

            yield break;
        }

        isConnected = true;

        Debug.Log("[VisionToSpeech] Live セッション準備完了。stream=" + streamMode);
    }

    // Setup メッセージ（最初に1回だけ送るセッション設定）
    string BuildSetupJson()
    {
        return GeminiLiveMessages.Setup(modelName, voiceName, mediaResolution,
            GeminiAudioResponse.WithAudioInstruction(GetSystemInstructionText()));
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

        if (streamMode)
        {
            AppendOutboundLog("stream ON（約 " + streamIntervalSeconds + "s 間隔）");
            streamLoopCoroutine = StartCoroutine(StreamLoopCoroutine());
        }
        else
        {
            StopStreamingInternal();
            AppendOutboundLog("stream OFF（シャッターへ）");
        }
    }

    // 約1 FPS でシャッターと同じ1ターン送信を繰り返す（前ターン未完了なら待つ）
    IEnumerator StreamLoopCoroutine()
    {
        // 直後に1回送り、以降は間隔待ち
        while (streamMode && isConnected)
        {
            if (!isTurnBusy && !IsAudioPending)
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

    // ----- Space シャッター -----

    // ストリーム OFF・非ビジーのときだけ Space Down で1フレーム送信
    void UpdateShutterInput()
    {
        if (streamMode || isTurnBusy || IsAudioPending || !isConnected || !setupComplete)
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
        if (framePromptField != null && framePromptField.isFocused)
            return true;
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

    // 画像とテキストを1つの clientContent に入れ、turnComplete で返答を求める
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

        int width;
        int height;
        byte[] jpegBytes;
        if (!TryCaptureJpeg(out jpegBytes, out width, out height))
        {
            ShowError("フレームの JPEG 化に失敗しました。");
            yield break;
        }
        yield return SendImageTurnCoroutine(jpegBytes, width, height, fromStream);
    }

    // キャプチャ後の共通経路。送信成功した JPEG だけを画面へ反映する。
    IEnumerator SendImageTurnCoroutine(byte[] jpegBytes, int width, int height, bool fromStream)
    {
        if (isTurnBusy || IsAudioPending || !isConnected || !setupComplete)
            yield break;
        response.Begin();
        SyncSystemInstructionBeforeSend();
        BeginDashboardTurn();

        // 画像と指示を同じターンに含め、画像の到着前に回答が始まる競合を防ぐ。
        // 音声用の activityStart/End は送らない（画像・テキストのみでは切断される）。
        string prompt = string.IsNullOrEmpty(framePromptText)
            ? "この画像を日本語で短く説明してください。"
            : framePromptText;
        prompt = GeminiAudioResponse.WithAudioInstruction(prompt);
        string turnJson = GeminiLiveMessages.ImageTurn(jpegBytes, prompt);
        Task sendTask = SendTextAsync(turnJson);
        while (!sendTask.IsCompleted)
            yield return null;
        if (sendTask.IsFaulted || sendTask.IsCanceled)
        {
            ShowError("画像の送信に失敗しました: " + (sendTask.Exception?.GetBaseException().Message ?? "キャンセル"));
            yield break;
        }

        outboundFrameCount++;
        outboundTotalBytes += jpegBytes.Length;
        ShowSentFrame(jpegBytes, width, height);
        AppendOutboundLog($"#{outboundFrameCount:000}  JPEG {width}×{height} / {jpegBytes.Length / 1024f:0.0} KB");
        AppendOutboundLog("clientContent\n    role: user\n    parts: inlineData + text\n    turnComplete: true");
        replyWaitStarted = Time.realtimeSinceStartup;
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
                                response.Cancel();
                                StopStreamingInternal();
                                if (!string.IsNullOrEmpty(closeReason))
                                {
                                    ShowError("切断: " + closeReason);
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
            EnqueueMain(() => { isConnected = false; StopStreamingInternal(); ShowError("受信ループ例外: " + e.Message); });
        }
    }

    // 共通パーサで受信イベントを読み、デモ固有の処理へ渡す。
    void HandleServerMessage(string json)
    {
        var message = GeminiLiveMessages.Parse(json);
        if (message.SetupComplete)
        {
            EnqueueMain(() => { setupComplete = true; AppendInboundLog("setupComplete / 接続済み"); });
            return;
        }
        foreach (var pcm in message.Audio)
        {
            playbackPcmQueue.Enqueue(pcm);
            EnqueueMain(() =>
            {
                response.AddAudio();
                RecordAudioChunk(pcm.Length);
                AppendInboundLog($"chunk {response.AudioChunks:00} / {pcm.Length / 1024f:0.0} KB / 累計 {turnAudioBytes / 1024f:0.0} KB");
            });
        }
        if (!string.IsNullOrEmpty(message.Transcript))
            EnqueueMain(() => OnOutputTranscription(message.Transcript));
        if (message.TurnComplete)
            EnqueueMain(OnTurnComplete);
        if (message.Interrupted)
            EnqueueMain(ClearPlaybackQueue);
    }

    void OnOutputTranscription(string fragment)
    {
        response.AppendTranscript(fragment);
        SetResponseCaption(response.Transcript);
        AppendInboundLog("outputTranscription: " + fragment);
    }

    // ターン完了: 最新の返答を保持し、ビジー解除
    void OnTurnComplete()
    {
        var completion = response.Complete();
        if (completion == GeminiResponseTurn.Completion.Ignored)
            return;
        if (completion == GeminiResponseTurn.Completion.RetryAudio)
        {
            StartCoroutine(RetryAudioCoroutine());
            return;
        }
        if (completion == GeminiResponseTurn.Completion.Failed)
        {
            StopStreamingInternal();
            ShowError(GeminiResponseTurn.MissingAudioError);
            return;
        }
        CompleteDashboardTurn();
        if (replyWaitStarted >= 0f)
        {
            ResponseTime.Log("合計", replyWaitStarted);
            replyWaitStarted = -1f;
        }

        AppendInboundLog("turnComplete");

        string modelText = (response.Transcript ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(modelText))
        {
            completedResponse = modelText;
        }

        SetResponseCaption(response.Transcript);
    }

    // ----- 再生 -----

    // 文字起こしだけで完了した場合、同じ回答の発話を1回だけ要求する。
    IEnumerator RetryAudioCoroutine()
    {
        Debug.LogWarning("[VisionToSpeech] 音声0件でturnComplete。音声を1回だけ再要求します。");
        AppendInboundLog("音声0件 / 同じ返答の音声を再要求（1/1）");
        yield return response.RetryAudio(SendTextAsync,
            () => socket != null && socket.State == WebSocketState.Open,
            ShowError, () => { StopStreamingInternal(); CloseSocket(false); });
    }

    IEnumerator PlaybackPumpCoroutine()
    {
        playbackCoroutineRunning = true;
        while (playbackCoroutineRunning)
        {
            // 境界を保たず連結して音声スレッドへ渡す。AudioSource.Playは起動時の1回だけ。
            while (playbackPcmQueue.TryDequeue(out byte[] pcm))
                pcmPlayer.Enqueue(pcm);
            if (!isTurnBusy)
                pcmPlayer.Complete();
            yield return null;
        }
    }

    void ClearPlaybackQueue()
    {
        while (playbackPcmQueue.TryDequeue(out byte[] ignored))
        {
        }
        if (pcmPlayer != null)
            pcmPlayer.Clear();
    }

    void ShowError(string message)
    {
        Debug.LogError("[VisionToSpeech] " + message);
        dashboardError = message;
        SetResponseCaption(message);
        response.Cancel();
        AppendInboundLog("[Error] " + message);
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
            if (pcmPlayer != null)
            {
                Destroy(pcmPlayer);
                pcmPlayer = null;
            }
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
