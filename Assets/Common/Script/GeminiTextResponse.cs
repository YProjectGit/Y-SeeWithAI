// GeminiTextResponse.cs
// REST の generateContent が返す JSON から、最初の候補のテキストを取り出す。
//
// 返ってくる形（必要なところだけ）:
//   {"candidates":[{"content":{"parts":[{"text":"..."}]}}]}
//
// JsonUtility は入れ子のクラスに載せる形でしか読めないので、必要なフィールドだけの
// 入れ物を用意して FromJson に渡している。どのフィールドを見るかがそのまま下の型。
//
// 使っているデモ: 1A / 2A / 2C / 3A

using System;
using UnityEngine;

/// <summary>
/// generateContent のレスポンスから candidates[0] のテキストを取り出す道具。
/// </summary>
public static class GeminiTextResponse
{
    // 取り出せたら true。失敗の内訳はログに出す（logPrefix は呼び出し元のデモ名）
    public static bool TryExtractText(string responseBody, string logPrefix, out string text)
    {
        text = null;
        if (string.IsNullOrEmpty(responseBody))
        {
            return false;
        }

        Root parsed = null;
        try
        {
            parsed = JsonUtility.FromJson<Root>(responseBody);
        }
        catch (Exception e)
        {
            Debug.LogError(logPrefix + " JSON 解析失敗: " + e.Message);
            return false;
        }

        if (parsed == null || parsed.candidates == null || parsed.candidates.Length == 0)
        {
            return false;
        }

        Candidate first = parsed.candidates[0];
        if (first == null || first.content == null || first.content.parts == null || first.content.parts.Length == 0)
        {
            return false;
        }

        text = first.content.parts[0].text;
        return !string.IsNullOrEmpty(text);
    }

    // ----- JsonUtility 用のレスポンス型（必要なフィールドだけ） -----

    [Serializable]
    class Root
    {
        public Candidate[] candidates; // 候補の配列。ふつうは1件目だけ使う
    }

    [Serializable]
    class Candidate
    {
        public Content content; // その候補の中身
    }

    [Serializable]
    class Content
    {
        public Part[] parts; // パーツの配列。テキストは1件目に入る
    }

    [Serializable]
    class Part
    {
        public string text; // 本文
    }
}
