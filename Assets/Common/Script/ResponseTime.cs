// ResponseTime.cs
// 送信開始から返信完了までの経過を、Unity Console に出す。
// 状態は持たない。呼び側が Time.realtimeSinceStartup で開始時刻を渡す。

using UnityEngine;

/// <summary>
/// 応答時間（ミリ秒）を Debug.Log する。
/// </summary>
public static class ResponseTime
{
    // [STT] 応答時間: 820 ms のように出す。stepName は STT / Chat / 合計 など
    public static void Log(string stepName, float startedRealtime)
    {
        float elapsedMs = (Time.realtimeSinceStartup - startedRealtime) * 1000f; // 送信からの経過（ms）
        Debug.Log("[" + stepName + "] 応答時間: " + elapsedMs.ToString("0") + " ms");
    }
}
