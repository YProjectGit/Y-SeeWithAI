// GeminiJson.cs
// JSON の文字列処理だけを集めた道具箱。通信そのものには関わらない。
//
// 各デモは「何を送るか」を自分の BuildRequestJson などで組み立てる。
// そのとき本文を JSON に埋めるためのエスケープと、画面に出すための整形をここが受け持つ。
//
// 使っているデモ: 1A / 1B / 2A / 2B / 2C / 3A / 3B / 3C / 4 / 5 / 6 / 7

using System;
using System.Text;
using UnityEngine;

/// <summary>
/// JSON 文字列のエスケープ・整形・省略表示を行う道具。状態を持たない。
/// </summary>
public static class GeminiJson
{
    // JSON 文字列用の最低限のエスケープ
    public static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        StringBuilder sb = new StringBuilder(value.Length + 8);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            switch (c)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }

    // インデントを軽く付けて読みやすくする（厳密なパーサではない）
    public static string PrettyPrint(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return string.Empty;
        }

        StringBuilder sb = new StringBuilder(json.Length + 32);
        int indent = 0;
        bool inString = false;
        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];
            if (c == '"' && (i == 0 || json[i - 1] != '\\'))
            {
                inString = !inString;
                sb.Append(c);
                continue;
            }

            if (inString)
            {
                sb.Append(c);
                continue;
            }

            switch (c)
            {
                case '{':
                case '[':
                    sb.Append(c);
                    sb.Append('\n');
                    indent++;
                    sb.Append(new string(' ', indent * 2));
                    break;
                case '}':
                case ']':
                    sb.Append('\n');
                    indent = Mathf.Max(0, indent - 1);
                    sb.Append(new string(' ', indent * 2));
                    sb.Append(c);
                    break;
                case ',':
                    sb.Append(c);
                    sb.Append('\n');
                    sb.Append(new string(' ', indent * 2));
                    break;
                case ':':
                    sb.Append(": ");
                    break;
                default:
                    if (!char.IsWhiteSpace(c))
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.ToString();
    }

    // 長い文字列を画面用に切り詰める（末尾に元の長さを添える）
    public static string Truncate(string value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
        {
            return value;
        }

        return value.Substring(0, maxChars) + "…(" + value.Length + " chars)";
    }

    // リクエスト JSON の "data":"..."（Base64 の音声・画像）だけを短く見せる
    // maxChars が 0 以下なら何もしない
    public static string TruncateBase64(string requestJson, int maxChars)
    {
        if (string.IsNullOrEmpty(requestJson) || maxChars <= 0)
        {
            return requestJson;
        }

        const string marker = "\"data\":\"";
        int dataIndex = requestJson.IndexOf(marker, StringComparison.Ordinal);
        if (dataIndex < 0)
        {
            return requestJson;
        }

        int valueStart = dataIndex + marker.Length;
        int valueEnd = requestJson.IndexOf('"', valueStart);
        if (valueEnd < 0)
        {
            return requestJson;
        }

        int length = valueEnd - valueStart;
        if (length <= maxChars)
        {
            return requestJson;
        }

        string head = requestJson.Substring(valueStart, maxChars);
        string replacement = head + "…(" + length + " chars total)";
        return requestJson.Substring(0, valueStart) + replacement + requestJson.Substring(valueEnd);
    }
}
