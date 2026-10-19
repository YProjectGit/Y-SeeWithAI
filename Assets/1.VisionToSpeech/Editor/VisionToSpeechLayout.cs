using System;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

// 承認済み3列レイアウトをSceneへ構築するEditor専用ツール。
public static class VisionToSpeechLayout
{
    static DashboardLayoutElements ui;
    static readonly Color Panel = new Color(.14f, .15f, .18f);
    static readonly Color Well = new Color(.09f, .10f, .12f);
    static readonly Color Muted = new Color(.66f, .70f, .76f);
    static readonly Color Blue = new Color(.12f, .48f, .92f);

    [MenuItem("Tools/VisionToSpeech/Rebuild Dashboard")]
    public static void Build()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (Application.isPlaying || scene.path != "Assets/1.VisionToSpeech/VisionToSpeech.unity")
            throw new InvalidOperationException("Open VisionToSpeech in EditMode first.");
        var demo = UnityEngine.Object.FindFirstObjectByType<VisionToSpeech>();
        var canvas = UnityEngine.Object.FindFirstObjectByType<Canvas>();
        ui = new DashboardLayoutElements(demo.systemInstructionField.textComponent.font);
        var oldChildren = new System.Collections.Generic.List<GameObject>();
        foreach (Transform child in canvas.transform)
            oldChildren.Add(child.gameObject);
        var inputTemplate = demo.systemInstructionField;
        Undo.RecordObject(demo, "Rebuild VisionToSpeech UI");
        var scaler = canvas.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
        scaler.matchWidthOrHeight = .5f;
        var root = ui.Rect("Dashboard", canvas.transform, 0, 0, 1920, 1080);
        root.anchorMin = root.anchorMax = new Vector2(.5f, .5f);
        root.pivot = new Vector2(.5f, .5f);
        root.anchoredPosition = Vector2.zero;
        ui.Fill(root, new Color(.075f, .08f, .10f));
        var left = ui.Box("CameraPanel", root, 24, 20, 612, 640);
        var center = ui.Box("SentPanel", root, 654, 20, 612, 640);
        var right = ui.Box("ResponsePanel", root, 1284, 20, 612, 640);
        var settings = ui.Box("SettingsPanel", root, 24, 680, 612, 380);
        var outgoing = ui.Box("OutboundPanel", root, 654, 680, 612, 380);
        var incoming = ui.Box("InboundPanel", root, 1284, 680, 612, 380);
        ui.Text("01  カメラ", left, 22, 18, 360, 40, 30);
        demo.cameraStateText = ui.Text("カメラ待機", left, 425, 22, 165, 32, 21, Muted, TextAlignmentOptions.MidlineRight);
        ui.Text("02  送信画像", center, 22, 18, 350, 40, 30);
        demo.connectionText = ui.Text("未接続", center, 445, 22, 145, 32, 21, Muted, TextAlignmentOptions.MidlineRight);
        demo.connectionLamp = ui.Fill(ui.Rect("ConnectionLamp", center, 424, 32, 12, 12), Muted);
        ui.Text("03  AIの返答", right, 22, 18, 345, 40, 30);
        demo.inboundStatusText = ui.Text("画像の送信待ち", right, 360, 22, 230, 32, 21, Blue, TextAlignmentOptions.MidlineRight);
        demo.webcamPreview = Preview(left, "CameraPreview", out demo.cameraAspect);
        ui.Text("現在のカメラ映像", left, 22, 400, 568, 28, 19, Muted);
        demo.sendFrameButton = ui.Button("画像を送信     [ Space ]", left, 22, 444, 568, 58, 25, Blue);
        demo.shutterModeButton = ui.Button("シャッター", left, 22, 524, 284, 50, 22, Blue);
        demo.streamModeButton = ui.Button("Stream", left, 306, 524, 284, 50, 22, Panel);
        demo.recordHintText = ui.Text("シャッター：Space またはボタンで1枚送信", left, 22, 592, 568, 30, 18, Muted, TextAlignmentOptions.Center);
        demo.sentFramePreview = Preview(center, "SentFramePreview", out demo.sentAspect);
        demo.sentFramePreview.enabled = false;
        demo.sentPlaceholderText = ui.Text("送信した画像をここに表示", center, 30, 210, 550, 60, 24, Muted, TextAlignmentOptions.Center);
        demo.sentFrameInfoText = ui.Text("まだ送信していません", center, 22, 400, 568, 30, 19, Muted);
        demo.sentTimeText = ui.Text("最終送信 —", center, 22, 443, 350, 30, 20, Muted);
        demo.outboundStatusText = ui.Text("待機中", center, 420, 443, 170, 30, 20, Blue, TextAlignmentOptions.MidlineRight);
        ui.Text("画像に添える指示", center, 22, 490, 568, 28, 19, Muted);
        demo.framePromptField = ui.Input(inputTemplate, center, 22, 524, 568, 70, 19);
        demo.framePromptField.text = demo.framePromptText;
        demo.framePromptField.readOnly = true;
        ui.Text("Inspectorで編集 / 次の画像送信から反映", center, 22, 602, 568, 25, 18, Muted);
        var response = ui.Box("Response", right, 22, 72, 568, 397, Well);
        demo.transcriptionText = ui.ScrollText(response, "LatestResponse", 18, 18, 532, 238, 30, out var responseScroll);
        demo.transcriptionText.text = "画像を送信すると、ここに返答が表示されます。";
        demo.responseFrameText = ui.Text("—", response, 20, 276, 528, 28, 19, Muted);
        demo.waveformBars = new RectTransform[48];
        for (int i = 0; i < 48; i++)
        {
            var bar = ui.Rect("Wave" + i, response, 23 + i * 11, 350, 5, 4);
            bar.pivot = new Vector2(.5f, .5f);
            ui.Fill(bar, Blue);
            demo.waveformBars[i] = bar;
        }
        ui.Text("これまでの返答", right, 22, 493, 568, 30, 22);
        demo.responseHistoryText = ui.ScrollText(right, "History", 22, 540, 568, 80, 20, out var historyScroll);
        demo.responseHistoryText.text = "まだ返答はありません";
        ui.Text("設定・接続情報", settings, 22, 14, 568, 34, 25);
        ui.Text("事前指示 / Inspectorで編集・次回Playから反映", settings, 22, 54, 568, 26, 17, Muted);
        demo.systemInstructionField = ui.Input(inputTemplate, settings, 22, 86, 568, 67, 19);
        demo.systemInstructionField.text = demo.systemInstructionText;
        demo.systemInstructionField.readOnly = true;
        demo.setupHeaderText = ui.ScrollText(settings, "Settings", 22, 164, 568, 200, 18, out var settingsScroll);
        demo.setupHeaderText.text = "Model    " + demo.modelName + "\nVoice    " + demo.voiceName + "\nResponse    AUDIO + 文字起こし\n画像    長辺 " + demo.maxSendLongSide + " px / JPEG " + demo.jpegQuality + "\nStream    " + demo.streamIntervalSeconds.ToString("0.0") + " 秒 / 返答・再生完了を待機\nSession    WebSocket / 未接続\nResolution    " + demo.mediaResolution;
        ui.Text("送信ログ", outgoing, 22, 14, 240, 34, 25);
        demo.outboundTotalsText = ui.Text("0 枚 / 累計 0 KB", outgoing, 274, 18, 316, 28, 18, Muted, TextAlignmentOptions.MidlineRight);
        demo.outboundLogText = ui.ScrollText(outgoing, "OutboundLog", 22, 62, 568, 300, 20, out demo.outboundScroll);
        demo.outboundLogText.text = "（まだ送信していません）";
        ui.Text("受信ログ", incoming, 22, 14, 240, 34, 25);
        demo.inboundHeaderText = ui.Text("PCM 24 kHz / mono", incoming, 290, 18, 300, 28, 18, Muted, TextAlignmentOptions.MidlineRight);
        demo.inboundLogText = ui.ScrollText(incoming, "InboundLog", 22, 62, 568, 238, 20, out demo.inboundScroll);
        demo.inboundLogText.text = "（まだ受信していません）";
        demo.inboundTotalsText = ui.Text("音声 0 chunks / 0 KB\n応答開始 — / 完了 —", incoming, 22, 314, 568, 54, 18, Muted);
        // 旧ヘッダ・チャット表示は新しい返答と履歴に置換。
        foreach (var child in oldChildren)
            Undo.DestroyObjectImmediate(child);
        EditorUtility.SetDirty(demo);
        EditorSceneManager.MarkSceneDirty(scene);
        Canvas.ForceUpdateCanvases();
    }

    static RawImage Preview(Transform p, string name, out AspectRatioFitter fit)
    {
        var well = ui.Box(name + "Well", p, 22, 72, 568, 320, Well);
        var rt = ui.Rect(name, well, 0, 0, 568, 320);
        var raw = rt.gameObject.AddComponent<RawImage>();
        raw.raycastTarget = false;
        raw.color = Color.white;
        raw.texture = Texture2D.blackTexture;
        fit = rt.gameObject.AddComponent<AspectRatioFitter>();
        fit.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
        fit.aspectRatio = 16f / 9f;
        return raw;
    }
}
