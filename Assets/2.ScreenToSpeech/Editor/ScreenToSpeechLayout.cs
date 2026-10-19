using System;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

public static class ScreenToSpeechLayout
{
    static DashboardLayoutElements ui;
    static readonly Color Panel = new Color(.14f, .15f, .18f);
    static readonly Color Well = new Color(.09f, .10f, .12f);
    static readonly Color Muted = new Color(.66f, .70f, .76f);
    static readonly Color Blue = new Color(.12f, .48f, .92f);
    [MenuItem("Tools/ScreenToSpeech/Rebuild Dashboard")]
    public static void Build()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (Application.isPlaying || scene.path != "Assets/2.ScreenToSpeech/ScreenToSpeech.unity")
            throw new InvalidOperationException("Open ScreenToSpeech in EditMode first.");
        var demo = UnityEngine.Object.FindFirstObjectByType<ScreenToSpeech>();
        var canvas = UnityEngine.Object.FindFirstObjectByType<Canvas>();
        ui = new DashboardLayoutElements(demo.captionText.font);
        var old = new System.Collections.Generic.List<GameObject>();
        foreach (Transform child in canvas.transform)
            old.Add(child.gameObject);
        Undo.RecordObject(demo, "Rebuild ScreenToSpeech UI");
        var scaler = canvas.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
        var root = ui.Rect("Dashboard", canvas.transform, 0, 0, 1920, 1080);
        root.anchorMin = root.anchorMax = root.pivot = new Vector2(.5f, .5f);
        root.anchoredPosition = Vector2.zero;
        ui.Fill(root, new Color(.075f, .08f, .10f));
        var left = ui.Box("DrawingPanel", root, 24, 20, 1160, 1040);
        ui.Text("描画", left, 22, 18, 180, 42, 32);
        ui.Text("描き足すと自動送信", left, 140, 28, 600, 30, 21, Muted);
        demo.clearButton = ui.Button("消す", left, 1010, 16, 128, 48, 24, Well);
        var pad = UnityEngine.Object.Instantiate(demo.drawingPad, left);
        pad.name = "DrawingPad";
        foreach (var fitter in pad.GetComponents<AspectRatioFitter>())
            UnityEngine.Object.DestroyImmediate(fitter);
        foreach (var layout in pad.GetComponents<LayoutElement>())
            UnityEngine.Object.DestroyImmediate(layout);
        var pr = (RectTransform)pad.transform;
        pr.anchorMin = pr.anchorMax = pr.pivot = new Vector2(0, 1);
        pr.anchoredPosition = new Vector2(20, -78);
        pr.sizeDelta = new Vector2(1120, 1120f / 1.5f);
        pad.textureWidth = 1152;
        pad.textureHeight = 768;
        pad.paperColor = Color.white;
        pad.GetComponent<RawImage>().color = Color.white;
        demo.drawingPad = pad;
        ui.Text("ドラッグして描く", left, 22, 833, 1100, 28, 19, Muted);
        var response = ui.Box("Response", left, 20, 875, 1120, 145, Well);
        demo.statusText = ui.Text("描いてください", response, 142, 20, 900, 30, 22, Blue);
        demo.captionText = ui.Text("描くと、ここに短い返答が表示されます。", response, 22, 62, 1076, 42, 26);
        demo.captionText.overflowMode = TextOverflowModes.Ellipsis;
        demo.responseFrameText = ui.Text("—", response, 22, 111, 1076, 26, 19, Muted);
        demo.waveformBars = new RectTransform[8];
        for (int i = 0; i < 8; i++)
        {
            var b = ui.Rect("Wave" + i, response, 28 + i * 12, 36, 6, 4);
            b.pivot = new Vector2(.5f, .5f);
            ui.Fill(b, Blue);
            demo.waveformBars[i] = b;
        }
        var sent = ui.Box("SentPanel", root, 1204, 20, 692, 486);
        ui.Text("送信画像", sent, 22, 18, 400, 42, 32);
        demo.connectionText = ui.Text("未接続", sent, 520, 24, 150, 30, 21, Muted, TextAlignmentOptions.MidlineRight);
        demo.connectionLamp = ui.Fill(ui.Rect("ConnectionLamp", sent, 495, 33, 13, 13), Muted);
        var well = ui.Box("SentWell", sent, 22, 78, 648, 318, Well);
        var img = ui.Rect("SentJPEG", well, 0, 0, 648, 318);
        img.pivot = new Vector2(.5f, .5f);
        demo.sentFramePreview = img.gameObject.AddComponent<RawImage>();
        demo.sentFramePreview.raycastTarget = false;
        demo.sentAspect = img.gameObject.AddComponent<AspectRatioFitter>();
        demo.sentAspect.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
        demo.sentAspect.aspectRatio = 1.5f;
        demo.sentFramePreview.enabled = false;
        demo.sentInfoText = ui.Text("まだ送信していません", sent, 22, 408, 648, 50, 19, Muted);
        demo.sentStateText = ui.Text("描くと自動送信", sent, 22, 461, 648, 25, 18, Blue);
        var logs = ui.Box("LogPanel", root, 1204, 524, 692, 424);
        ui.Text("送受信ログ", logs, 22, 18, 260, 40, 30);
        demo.totalsText = ui.Text("送信 0 枚    音声 0 chunks", logs, 280, 27, 390, 30, 19, Muted, TextAlignmentOptions.MidlineRight);
        demo.logText = ui.ScrollText(logs, "Events", 22, 76, 648, 326, 21, out demo.logScroll);
        demo.logText.text = "接続するとログが表示されます";
        var settings = ui.Box("Settings", root, 1204, 966, 692, 94);
        demo.settingsText = ui.Text("Kore · AUDIO / 24 kHz · 返答40文字以内\n自動送信 1秒間隔 · 返答と再生の完了を待機", settings, 22, 18, 648, 62, 20, Muted);
        demo.responseCharacterLimit = 40;
        demo.systemInstructionText = demo.systemInstructionText.Replace("返答は1〜2文にしてください。", "返答は1文にしてください。");
        foreach (var child in old)
            Undo.DestroyObjectImmediate(child);
        EditorUtility.SetDirty(demo);
        EditorSceneManager.MarkSceneDirty(scene);
        Canvas.ForceUpdateCanvases();
    }
}
