// GeminiJsonScan.cs
// Live API の受信 JSON から、目当ての文字列だけを素朴に拾う道具。
//
// Live API は JsonUtility では扱いにくい形（入れ子と可変キー）で返ってくるため、
// 「このキーの後ろにある文字列」を前から順に探す方式にしている。
// どのキーを探すかは各デモが決める。ここは探し方だけを持つ。
//
// 使っているデモ: 3B / 3C / 4 / 5

using System;
using System.Text;

/// <summary>
/// JSON テキストからキーを頼りに文字列を取り出す道具。状態を持たない。
/// </summary>
public static class GeminiJsonScan
{
    // objectKey の後ろにある最初の "text" の値を返す（見つからなければ null）
    public static string NestedTextAfterKey(string json, string objectKey)
    {
        int keyIndex = json.IndexOf("\"" + objectKey + "\"", StringComparison.Ordinal);
        if (keyIndex < 0)
        {
            return null;
        }

        int textKey = json.IndexOf("\"text\"", keyIndex, StringComparison.Ordinal);
        if (textKey < 0)
        {
            return null;
        }

        return StringFieldFrom(json, textKey);
    }

    // keyIndex にあるキーの値（文字列）を読む。\n \" \\ のエスケープだけ戻す
    public static string StringFieldFrom(string json, int keyIndex)
    {
        int colon = json.IndexOf(':', keyIndex);
        if (colon < 0)
        {
            return null;
        }

        int firstQuote = json.IndexOf('"', colon + 1);
        if (firstQuote < 0)
        {
            return null;
        }

        int i = firstQuote + 1;
        StringBuilder sb = new StringBuilder();
        while (i < json.Length)
        {
            char c = json[i];
            if (c == '\\' && i + 1 < json.Length)
            {
                char n = json[i + 1];
                if (n == 'n')
                {
                    sb.Append('\n');
                }
                else if (n == '"')
                {
                    sb.Append('"');
                }
                else if (n == '\\')
                {
                    sb.Append('\\');
                }
                else
                {
                    sb.Append(n);
                }

                i += 2;
                continue;
            }

            if (c == '"')
            {
                break;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }
}
