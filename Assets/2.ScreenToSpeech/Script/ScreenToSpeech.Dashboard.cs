using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public partial class ScreenToSpeech
{
    [Header("Dashboard")]
    public RawImage sentFramePreview;
    public AspectRatioFitter sentAspect;
    public TMP_Text sentInfoText, sentStateText, connectionText, logText, totalsText, settingsText, responseFrameText;
    public Image connectionLamp;
    public ScrollRect logScroll;
    public RectTransform[] waveformBars;
    public TMP_Text systemInstructionDisplay, framePromptDisplay;
    string appliedSystemInstruction;
    Texture2D sentTexture;
    int sentCount;
    long turnAudioBytes;
    string dashboardError;
    readonly List<string> eventLog = new List<string>();
    readonly float[] waveformSamples = new float[256];
    float nextRefresh;
    string statusMessage = "接続中…";
    string captionMessage = string.Empty;

    void SetStatus(string text)
    {
        statusMessage = text;
    }
    void SetCaption(string text)
    {
        captionMessage = text ?? string.Empty;
    }


    void InitializeDashboard()
    {
        if (sentFramePreview != null)
            sentFramePreview.enabled = false;
        if (sentInfoText != null)
            sentInfoText.text = "まだ送信していません";
        if (responseFrameText != null)
            responseFrameText.text = "1ストローク描き終えると送信します";
        UpdateDashboard();
        Canvas.ForceUpdateCanvases();
        if (systemInstructionDisplay != null)
            systemInstructionDisplay.GetComponentInParent<ScrollRect>().verticalNormalizedPosition = 1f;
        if (framePromptDisplay != null)
            framePromptDisplay.GetComponentInParent<ScrollRect>().verticalNormalizedPosition = 1f;
    }

    public void RefreshInstructionDisplay()
    {
        if (systemInstructionDisplay != null)
            systemInstructionDisplay.text = appliedSystemInstruction ?? systemInstructionText ?? string.Empty;
        if (framePromptDisplay != null)
            framePromptDisplay.text = framePromptText ?? string.Empty;
    }
    void RecordSentFrame(byte[] jpeg, int width, int height)
    {
        sentCount++;
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
        }
        if (sentInfoText != null)
            sentInfoText.text = $"#{sentCount:000}   {width} × {height} px   JPEG {jpegQuality}\n最終送信 {DateTime.Now:HH:mm:ss}   ·   {jpeg.Length / 1024f:0.0} KB";
        if (responseFrameText != null)
            responseFrameText.text = $"#{sentCount:000} への返答";
        AddLog("送信", $"#{sentCount:000} / {jpeg.Length / 1024f:0.0} KB");
    }
    void RecordAudio(int bytes)
    {
        turnAudioBytes += bytes;
        if (response.AudioChunks == 1)
            AddLog("受信", $"音声開始 / {Mathf.Max(0, Time.realtimeSinceStartup - replyWaitStarted):0.00}秒");
    }
    void RecordTurnComplete()
    {
        AddLog("音声", $"{response.AudioChunks} chunks / {turnAudioBytes / 1024f:0.0} KB");
        AddLog("完了", $"{Mathf.Max(0, Time.realtimeSinceStartup - replyWaitStarted):0.00}秒");
    }
    void AddLog(string kind, string detail)
    {
        eventLog.Add($"{DateTime.Now:HH:mm:ss}  {kind}  {detail}");
        if (eventLog.Count > 80)
            eventLog.RemoveAt(0);
        if (logText != null)
            logText.text = string.Join("\n", eventLog);
        if (logScroll != null)
        {
            Canvas.ForceUpdateCanvases();
            logScroll.verticalNormalizedPosition = 0;
        }
    }
    void UpdateDashboard()
    {
        bool playing = pcmPlayer != null && pcmPlayer.HasPendingAudio;
        if (playing)
            pcmPlayer.GetOutputData(waveformSamples);
        if (waveformBars != null)
            for (int i = 0; i < waveformBars.Length; i++)
            {
                if (waveformBars[i] == null)
                    continue;
                float peak = 0;
                if (playing)
                    for (int j = i * waveformSamples.Length / waveformBars.Length; j < (i + 1) * waveformSamples.Length / waveformBars.Length; j++)
                        peak = Mathf.Max(peak, Mathf.Abs(waveformSamples[j]));
                var size = waveformBars[i].sizeDelta;
                size.y = Mathf.Lerp(size.y, 4 + peak * 34, .4f);
                waveformBars[i].sizeDelta = size;
            }
        if (Time.unscaledTime < nextRefresh)
            return;
        nextRefresh = Time.unscaledTime + .1f;
        RefreshInstructionDisplay();
        bool connected = isConnected && setupComplete;
        if (statusText != null)
            statusText.text = dashboardError != null ? "エラー" : playing ? "話しています"
                : response.IsBusy ? (response.RetryUsed ? "音声を再要求しています…" : "見てます…")
                : connected ? "描いてください" : statusMessage;
        if (captionText != null)
            captionText.text = captionMessage;
        if (connectionText != null)
            connectionText.text = connected ? "接続済み" : "未接続";
        if (connectionLamp != null)
            connectionLamp.color = connected ? new Color(.23f, .85f, .55f) : Color.gray;
        if (sentStateText != null)
            sentStateText.text = dashboardError != null ? dashboardError
                : drawingPad != null && drawingPad.IsDrawing ? "描画中 · 離すと送信"
                : drawingPad != null && drawingPad.HasCompletedStroke ? "ストローク完了 · 送信待ち"
                : isTurnBusy ? "返答待ち" : sentCount > 0 ? "最新の描画を送信済み" : "1ストローク終了時に送信";
        if (totalsText != null)
            totalsText.text = $"送信 {sentCount} 枚    音声 {response.AudioChunks} chunks";
        if (settingsText != null)
            settingsText.text = $"{voiceName}  ·  AUDIO / {playbackSampleRate / 1000} kHz  ·  返答 {responseCharacterLimit}文字以内\n1ストローク終了時に送信 · 返答と再生の完了を待機";
    }
}
