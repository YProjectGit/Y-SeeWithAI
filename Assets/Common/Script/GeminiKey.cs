// GeminiKey.cs
// APIキーの読み込みと、画面表示用のマスク、generateContent の URL 組み立て。
//
// キーは Assets/Common/APIKey.txt に置く（ダミーを本物のキー1行に置き換える）。
// 取得手順は Assets/Docs/gemini-ai-studio-setup.md を参照。
// ダミー文言のままでは未設定として扱う。
//
// ここは「読む」だけで、失敗をどう見せるか（Status 欄・Response 欄・ログの接頭辞）は
// 各デモが決める。TryRead が false を返したら error にその理由が入る。
//
// 使っているデモ: 1A / 1B / 2A / 2B / 2C / 3A / 3B / 3C / 4 / 5 / 6 / 7

using System.IO;
using UnityEngine;

/// <summary>
/// APIキーの読み込みと URL 組み立て。UI もログも触らない。
/// </summary>
public static class GeminiKey
{
    // Assets/ からの相対パスで APIキーファイルを1行読む
    // 成功で true。失敗のとき key は null、error に日本語の理由が入る
    public static bool TryRead(string relativePath, out string key, out string error)
    {
        key = null;
        error = null;

        string path = Path.Combine(Application.dataPath, relativePath);
        if (!File.Exists(path))
        {
            error = "APIキーファイルがありません: " + path;
            return false;
        }

        string raw = File.ReadAllText(path).Trim();
        if (string.IsNullOrEmpty(raw) || raw == "Paste Your APIKey Here…")
        {
            error = "APIキーが空です。Assets/Docs/gemini-ai-studio-setup.md を参照してください。（" + path + "）";
            return false;
        }

        key = raw;
        return true;
    }

    // 画面表示用にキーを伏せる（先頭数文字だけ残す）
    public static string Mask(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return "(none)";
        }

        if (key.Length <= 6)
        {
            return "******";
        }

        return key.Substring(0, 4) + "…" + new string('*', 8);
    }

    // REST の generateContent エンドポイント（モデル名が URL の一部になる）
    public static string BuildGenerateContentUrl(string modelName)
    {
        return "https://generativelanguage.googleapis.com/v1beta/models/"
               + modelName
               + ":generateContent";
    }
}
