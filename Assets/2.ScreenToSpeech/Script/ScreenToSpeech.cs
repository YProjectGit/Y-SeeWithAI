// ScreenToSpeech.cs
// 1.VisionToSpeech の姉妹デモ。WebCam ではなく、描画パッドの JPEG を Live API に送る。
// 左に描画と短い字幕、右に送信JPEGと簡潔な送受信ログを表示する。
//
// 上からの流れ:
//   Start → APIキー・体験 UI・AudioSource・Live 接続（Setup）
//   描く → dirty なら約1秒間隔で
//     画像と実況指示を clientContent の1ターンにまとめて送信 → 音声返答
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
public partial class ScreenToSpeech : MonoBehaviour
{
    // ===== インスペクタ: 設定 =====

    public string modelName = "gemini-3.1-flash-live-preview"; // Live モデル名（Setup の models/ 以下）
    public string apiKeyRelativePath = "Common/APIKey.txt"; // Assets/ からの相対パス
    public string voiceName = "Kore"; // Setup の prebuilt 声色名
    [Range(0.05f, 0.5f)] public float playbackBufferSeconds = 0.15f; // 通信の揺れを吸収する先読み秒数
    public int playbackSampleRate = 24000; // 受信 PCM の想定サンプルレート（Hz）
    public int maxSendLongSide = 768; // 送信 JPEG の長辺上限（推奨解像度）
    public int jpegQuality = 75; // EncodeToJPG の品質（1〜100）
    public float interpretIntervalSeconds = 1f; // 自動送信の間隔（約1 FPS）
    public string mediaResolution = "MEDIA_RESOLUTION_MEDIUM"; // generationConfig の mediaResolution
    public string systemInstructionText =
        "あなたは、人がいま描いている絵を見る実況者です。紙に見えている線や形を、日本語で短くやさしく話してください。途中の線でも、いま見えているものから推測してよいです。まだほとんど描かれていないときは、無理に物語を作らず、見えていることだけを言ってください。返答は1文にしてください。"; // Setup の事前指示（画面には出さない）
    [Range(20, 100)] public int responseCharacterLimit = 40; // 音声と字幕の返答長を指示
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
    readonly GeminiResponseTurn response = new GeminiResponseTurn();
    bool isTurnBusy => response.IsBusy;
    bool IsAudioPending => !playbackPcmQueue.IsEmpty || (pcmPlayer != null && pcmPlayer.HasPendingAudio);
    PcmStreamPlayer pcmPlayer;
    bool playbackCoroutineRunning; // 再生コルーチン稼働中か
    float replyWaitStarted = -1f; // 画像送信開始時刻。未計測は -1

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
        pcmPlayer = playbackAudioSource.GetComponent<PcmStreamPlayer>();
        if (pcmPlayer == null)
            pcmPlayer = playbackAudioSource.gameObject.AddComponent<PcmStreamPlayer>();
        pcmPlayer.Initialize(playbackSampleRate, playbackBufferSeconds);
        WireClearButton();
        SetCaption(string.Empty);
        InitializeDashboard();

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
        UpdateDashboard();
    }

    // 終了時にソケットを解放する
    void OnDestroy()
    {
        CloseSocket(true);
        if (sentTexture != null)
            Destroy(sentTexture);
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
        return GeminiLiveMessages.Setup(modelName, voiceName, mediaResolution,
            GeminiAudioResponse.WithAudioInstruction(systemInstructionText + ShortReplyInstruction()));
    }

    string ShortReplyInstruction() => $"\n返答は日本語で{Mathf.Clamp(responseCharacterLimit, 20, 100)}文字以内、短い1文だけにしてください。前置きや質問を付けず、最も目立つ特徴だけを話してください。";

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

            if (!isConnected || !setupComplete || isTurnBusy || IsAudioPending)
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

    // 画像とテキストを1つの clientContent に入れ、turnComplete で返答を求める
    IEnumerator SendFrameTurnCoroutine()
    {
        if (isTurnBusy || IsAudioPending)
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

        response.Begin();
        dashboardError = null;
        turnAudioBytes = 0;
        drawingPad.MarkClean();
        ClearPlaybackQueue();
        SetStatus("見てます…");

        // 画像と指示を同じターンに含め、画像の到着前に回答が始まる競合を防ぐ。
        // 音声用の activityStart/End は送らない（画像・テキストのみでは切断される）。
        string prompt = string.IsNullOrEmpty(framePromptText)
            ? "この絵を日本語で短く実況してください。"
            : framePromptText;
        prompt = GeminiAudioResponse.WithAudioInstruction(prompt + ShortReplyInstruction());
        string turnJson = GeminiLiveMessages.ImageTurn(jpegBytes, prompt);
        replyWaitStarted = Time.realtimeSinceStartup;
        Task send = SendTextAsync(turnJson);
        while (!send.IsCompleted)
            yield return null;
        if (send.IsFaulted || send.IsCanceled)
        {
            ShowError("画像の送信に失敗しました。");
            yield break;
        }
        RecordSentFrame(jpegBytes, width, height);

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
                                if (!string.IsNullOrEmpty(closeReason))
                                    ShowError("切断: " + closeReason);
                                else
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

    // 共通パーサで受信イベントを読み、デモ固有の処理へ渡す。
    void HandleServerMessage(string json)
    {
        var message = GeminiLiveMessages.Parse(json);
        if (message.SetupComplete)
        {
            EnqueueMain(() => { setupComplete = true; AddLog("接続", "セッション準備完了"); });
            return;
        }
        foreach (var pcm in message.Audio)
        {
            playbackPcmQueue.Enqueue(pcm);
            EnqueueMain(() =>
            {
                response.AddAudio();
                RecordAudio(pcm.Length);
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
        SetCaption(response.Transcript.Trim());
        SetStatus("話しています");
    }

    // ターン完了: ビジー解除。字幕は残す
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
            ShowError(GeminiResponseTurn.MissingAudioError);
            return;
        }
        RecordTurnComplete();
        if (replyWaitStarted >= 0f)
        {
            ResponseTime.Log("合計", replyWaitStarted);
            replyWaitStarted = -1f;
        }

        SetCaption(response.Transcript.Trim());
        if (!IsAudioPending)
        {
            SetStatus("描いてください");
        }
    }

    // ----- 再生 -----

    // 文字起こしだけで完了した場合、同じ回答の発話を1回だけ要求する。
    IEnumerator RetryAudioCoroutine()
    {
        Debug.LogWarning("[ScreenToSpeech] 音声0件でturnComplete。音声を1回だけ再要求します。");
        AddLog("再要求", "音声0件 / 1回だけ再要求");
        yield return response.RetryAudio(SendTextAsync,
            () => socket != null && socket.State == WebSocketState.Open,
            ShowError, () => { CloseSocket(false); });
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
        AddLog("描画", "キャンバスをクリア");
    }

    void ShowError(string message)
    {
        dashboardError = message;
        AddLog("エラー", message);
        Debug.LogError("[ScreenToSpeech] " + message);
        SetStatus("エラー");
        response.Cancel();
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
        // Start の実行順によっては既存の EventSystem がまだ current に登録されていない。
        if (EventSystem.current != null || FindFirstObjectByType<EventSystem>() != null)
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
