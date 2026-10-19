using UnityEditor;

[CustomEditor(typeof(ScreenToSpeech))]
public class ScreenToSpeechInspector : Editor
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
            ((ScreenToSpeech)target).ExportInstructionFiles();
    }

    public override void OnInspectorGUI()
    {
        EditorGUILayout.HelpBox(
            "指示はここで編集します。事前指示は次回Play、画像に添える指示は次の送信から反映します。" +
            "永続的な変更はPlay停止中に行い、Sceneを保存してください。\n" +
            "文章は2.ScreenToSpeech内のSystemInstruction.txt / FramePrompt.txtにも自動出力します。" +
            "音声出力とResponse Character Limitの条件はアプリ側で追加します。", MessageType.Info);

        if (DrawDefaultInspector())
        {
            var demo = (ScreenToSpeech)target;
            if (demo.systemInstructionDisplay != null)
                Undo.RecordObject(demo.systemInstructionDisplay, "Update instruction display");
            if (demo.framePromptDisplay != null)
                Undo.RecordObject(demo.framePromptDisplay, "Update instruction display");
            demo.ExportInstructionFiles();
            demo.RefreshInstructionDisplay();
        }
    }
}
