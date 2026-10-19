using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// 画面表示。通信は VisionToSpeech.cs が担当する。
public partial class VisionToSpeech
{
    [Header("Dashboard / 入力・送信画像")]
    public Button sendFrameButton;
    public Button shutterModeButton;
    public TMP_InputField framePromptField;
    public RawImage sentFramePreview;
    public TMP_Text sentFrameInfoText;
    public TMP_Text sentTimeText;
    public TMP_Text sentPlaceholderText;
    public TMP_Text cameraStateText;
    public TMP_Text connectionText;
    public Image connectionLamp;
    public AspectRatioFitter cameraAspect;
    public AspectRatioFitter sentAspect;
    [Header("Dashboard / 返答・ログ")]
    public TMP_Text responseFrameText;
    public TMP_Text responseHistoryText;
    public TMP_Text outboundTotalsText;
    public TMP_Text inboundTotalsText;
    public ScrollRect outboundScroll;
    public ScrollRect inboundScroll;
    public RectTransform[] waveformBars;

    Texture2D sentTexture;
    string responseCaption;
    const int MaxLogChars = 8000; // ログ欄の上限
    readonly StringBuilder inboundLog = new StringBuilder(); // 受信ログ本文
    readonly StringBuilder outboundLog = new StringBuilder(); // 送信ログ本文

    void SetResponseCaption(string text)
    {
        responseCaption = text;
    }
    string completedResponse;
    string dashboardError;
    readonly List<string> responseHistory = new List<string>();
    long turnAudioBytes;
    float firstAudioSeconds = -1f;
    float completedSeconds = -1f;
    float nextDashboardRefresh;
    readonly float[] waveformSamples = new float[256];
    static readonly Color DashboardBlue = new Color(0.12f, 0.48f, 0.92f);
    static readonly Color DashboardMuted = new Color(0.21f, 0.23f, 0.28f);
    bool IsAudioPending => !playbackPcmQueue.IsEmpty || (pcmPlayer != null && pcmPlayer.HasPendingAudio);

    void InitializeDashboard()
    {
        if (sendFrameButton != null)
            sendFrameButton.onClick.AddListener(SendFromButton);
        if (shutterModeButton != null)
            shutterModeButton.onClick.AddListener(() => { if (streamMode) OnStreamModeButtonClicked(); });
        if (framePromptField != null)
        {
            framePromptField.SetTextWithoutNotify(framePromptText);
            framePromptField.readOnly = true;
        }
        SetResponseCaption("画像を送信すると、ここに返答が表示されます。");
        if (sentFramePreview != null)
            sentFramePreview.enabled = false;
        if (sentPlaceholderText != null)
            sentPlaceholderText.gameObject.SetActive(true);
        if (systemInstructionField != null)
            systemInstructionField.readOnly = true;
        UpdateDashboard();
    }

    void SendFromButton()
    {
        if (!streamMode && !isTurnBusy && !IsAudioPending && isConnected && setupComplete)
            StartCoroutine(SendFrameTurnCoroutine(false));
    }

    void BeginDashboardTurn()
    {
        if (!string.IsNullOrEmpty(completedResponse))
        {
            responseHistory.Insert(0, $"#{outboundFrameCount:000}  {completedResponse}");
            if (responseHistory.Count > 20)
                responseHistory.RemoveAt(responseHistory.Count - 1);
            if (responseHistoryText != null)
                responseHistoryText.text = string.Join("\n\n", responseHistory);
            completedResponse = null;
        }
        dashboardError = null;
        turnAudioBytes = 0;
        firstAudioSeconds = -1f;
        completedSeconds = -1f;
        SetResponseCaption("画像を送信しています…");
    }

    void ShowSentFrame(byte[] jpeg, int width, int height)
    {
        // 送信バイト列そのものをデコード。未圧縮カメラ画像で代用しない。
        if (sentTexture == null)
            sentTexture = new Texture2D(2, 2, TextureFormat.RGB24, false);
        if (sentTexture.LoadImage(jpeg))
        {
            if (sentFramePreview != null)
            {
                sentFramePreview.texture = sentTexture;
                sentFramePreview.enabled = true;
            }
            if (sentAspect != null)
                sentAspect.aspectRatio = (float)width / height;
            if (sentPlaceholderText != null)
                sentPlaceholderText.gameObject.SetActive(false);
        }
        if (sentFrameInfoText != null)
            sentFrameInfoText.text = $"#{outboundFrameCount:000}  ·  {width} × {height} px  ·  JPEG {jpegQuality}  ·  {jpeg.Length / 1024f:0.0} KB";
        if (sentTimeText != null)
            sentTimeText.text = "最終送信 " + DateTime.Now.ToString("HH:mm:ss");
        if (responseFrameText != null)
            responseFrameText.text = $"#{outboundFrameCount:000} への返答";
        SetResponseCaption("画像についての返答を待っています…");
    }

    void RecordAudioChunk(int bytes)
    {
        turnAudioBytes += bytes;
        if (firstAudioSeconds < 0f && replyWaitStarted >= 0f)
        {
            firstAudioSeconds = Time.realtimeSinceStartup - replyWaitStarted;
            AppendInboundLog($"#{outboundFrameCount:000} 最初の音声 / {firstAudioSeconds:0.00} 秒\n    audio/pcm;rate={playbackSampleRate}");
        }
    }

    void CompleteDashboardTurn()
    {
        // OnTurnComplete のタイマー破棄前に記録する。
        if (replyWaitStarted >= 0f)
            completedSeconds = Time.realtimeSinceStartup - replyWaitStarted;
    }

    void UpdateDashboard()
    {
        UpdateWaveform();
        if (Time.unscaledTime < nextDashboardRefresh)
            return;
        nextDashboardRefresh = Time.unscaledTime + 0.1f;
        if (transcriptionText != null && responseCaption != null)
            transcriptionText.text = responseCaption;
        bool cameraReady = webCamTexture != null && webCamTexture.isPlaying && webCamTexture.width >= 16;
        bool connected = isConnected && setupComplete;
        if (connectionText != null)
            connectionText.text = connected ? "接続済み" : (dashboardError == null ? "未接続" : "接続エラー");
        if (connectionLamp != null)
            connectionLamp.color = connected ? new Color(0.23f, 0.91f, 0.65f) : new Color(0.55f, 0.58f, 0.64f);
        if (cameraStateText != null)
            cameraStateText.text = cameraReady ? "● LIVE" : "カメラ待機";
        if (cameraReady && cameraAspect != null)
            cameraAspect.aspectRatio = (float)webCamTexture.width / webCamTexture.height;
        if (sendFrameButton != null)
            sendFrameButton.interactable = connected && cameraReady && !streamMode && !isTurnBusy && !IsAudioPending;
        if (shutterModeButton != null)
            shutterModeButton.interactable = connected;
        if (streamModeButton != null)
            streamModeButton.interactable = connected && cameraReady;
        if (shutterModeButton != null)
            shutterModeButton.image.color = streamMode ? DashboardMuted : DashboardBlue;
        if (streamModeButtonImage != null)
            streamModeButtonImage.color = streamMode ? DashboardBlue : DashboardMuted;
        if (streamModeButtonLabel != null)
            streamModeButtonLabel.text = "Stream";
        if (recordHintText != null)
            recordHintText.text = streamMode ? "Stream：返答と音声再生が終わると次を送信" : "シャッター：Space またはボタンで1枚送信";
        RefreshInstructionDisplay();
        if (setupHeaderText != null)
            setupHeaderText.text = BuildSetupSettingsSummary();
        if (outboundStatusText != null)
            outboundStatusText.text = dashboardError != null ? "送信エラー" : outboundFrameCount > 0 ? "送信済み" : "待機中";
        if (outboundTotalsText != null)
            outboundTotalsText.text = $"{outboundFrameCount} 枚 / 累計 {outboundTotalBytes / 1024f:0.0} KB";
        if (inboundTotalsText != null)
            inboundTotalsText.text = $"音声 {response.AudioChunks} chunks / {turnAudioBytes / 1024f:0.0} KB\n応答開始 {(firstAudioSeconds < 0f ? "—" : firstAudioSeconds.ToString("0.00") + " 秒")} / 完了 {(completedSeconds < 0f ? "—" : completedSeconds.ToString("0.00") + " 秒")}";
        if (inboundStatusText != null)
            inboundStatusText.text = dashboardError != null ? "エラー" : IsAudioPending ? "音声を再生中" : isTurnBusy ? "返答待ち" : outboundFrameCount == 0 ? "画像の送信待ち" : "返答完了";
        if (systemInstructionField != null)
            systemInstructionField.readOnly = true;
    }

    void UpdateWaveform()
    {
        if (waveformBars == null || waveformBars.Length == 0)
            return;
        bool playing = pcmPlayer != null && pcmPlayer.HasPendingAudio;
        if (playing)
            pcmPlayer.GetOutputData(waveformSamples);
        for (int i = 0; i < waveformBars.Length; i++)
        {
            if (waveformBars[i] == null)
                continue;
            float peak = 0f;
            if (playing)
            {
                int start = i * waveformSamples.Length / waveformBars.Length;
                int end = (i + 1) * waveformSamples.Length / waveformBars.Length;
                for (int j = start; j < end; j++)
                    peak = Mathf.Max(peak, Mathf.Abs(waveformSamples[j]));
            }
            var size = waveformBars[i].sizeDelta;
            size.y = Mathf.Lerp(size.y, 4f + Mathf.Clamp01(peak * 4f) * 54f, 0.35f);
            waveformBars[i].sizeDelta = size;
        }
    }

    static void FollowLog(ScrollRect scroll)
    {
        if (scroll == null)
            return;
        if (scroll.content.rect.height > scroll.viewport.rect.height && scroll.verticalNormalizedPosition > 0.05f)
            return;
        Canvas.ForceUpdateCanvases();
        scroll.verticalNormalizedPosition = 0f;
    }
    void InitPanelTexts()
    {
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
            SetResponseCaption("（transcription 待ち）");
        }

        SetInboundHeaderStatic();
        if (setupHeaderText != null)
        {
            setupHeaderText.text =
                BuildSetupSettingsSummary();
        }
    }
    string BuildSetupSettingsSummary()
    {
        return "Model    " + modelName + "\n"
            + "Voice    " + voiceName + "\n"
            + "Response    AUDIO + 文字起こし\n"
            + "画像    長辺 " + maxSendLongSide + " px / JPEG " + jpegQuality + "\n"
            + "Stream    " + streamIntervalSeconds.ToString("0.0") + " 秒 / 返答・再生完了を待機\n"
            + "Session    WebSocket / " + (isConnected ? "接続済み" : "未接続") + "\n"
            + "Resolution    " + mediaResolution;
    }
    void SetInboundHeaderStatic()
    {
        if (inboundHeaderText == null)
        {
            return;
        }

        inboundHeaderText.text = "PCM " + (playbackSampleRate / 1000) + " kHz / mono";
    }
    void AppendOutboundLog(string line)
    {
        AppendLog(outboundLog, outboundLogText, line);
        FollowLog(outboundScroll);
    }
    void AppendInboundLog(string line)
    {
        AppendLog(inboundLog, inboundLogText, line);
        FollowLog(inboundScroll);
    }
    static void AppendLog(StringBuilder sb, TMP_Text view, string line)
    {
        if (sb.Length > 0)
        {
            sb.Append('\n');
        }

        sb.Append(DateTime.Now.ToString("HH:mm:ss") + "  " + line);
        if (sb.Length > MaxLogChars)
        {
            sb.Remove(0, sb.Length - MaxLogChars);
        }

        if (view != null)
        {
            view.text = sb.ToString();
        }
    }
}
