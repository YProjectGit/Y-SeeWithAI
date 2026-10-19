using UnityEditor;
using UnityEngine;

// 指示の編集元をコンポーネントに一本化し、表示と配布用テキストを同期する。
[CustomEditor(typeof(VisionToSpeech))]
public class VisionToSpeechInspector : Editor
{
    void OnEnable()
    {
        Undo.undoRedoPerformed += SyncInstructionFiles;
        SyncInstructionFiles();
    }

    void OnDisable() => Undo.undoRedoPerformed -= SyncInstructionFiles;

    void SyncInstructionFiles()
    {
        if (target != null)
            ((VisionToSpeech)target).ExportInstructionFiles();
    }

    public override void OnInspectorGUI()
    {
        EditorGUILayout.HelpBox(
            "指示はここで編集します。事前指示は次回Play、画像に添える指示は次の送信から反映します。" +
            "画面は確認専用です。永続的な変更はPlay停止中に行い、Sceneを保存してください。\n" +
            "文章は1.VisionToSpeech内のSystemInstruction.txt / FramePrompt.txtにも自動出力します。" +
            "音声で返答する条件はアプリ側で追加します。", MessageType.Info);

        if (!DrawDefaultInspector())
            return;

        var demo = (VisionToSpeech)target;
        if (!Application.isPlaying)
        {
            if (demo.systemInstructionField != null)
                Undo.RecordObject(demo.systemInstructionField, "Update instruction display");
            if (demo.framePromptField != null)
                Undo.RecordObject(demo.framePromptField, "Update instruction display");
        }
        demo.ExportInstructionFiles();
        demo.RefreshInstructionDisplay();
    }
}
