// ChatBubble.cs
// チャット1件分の吹き出し。各デモが Instantiate して SetMessage で中身を書き込む。

using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 話者名・本文・User/Assistant の見た目を持つメッセージバブル。
/// </summary>
public class ChatBubble : MonoBehaviour
{
    public TMP_Text speakerText; // 「You」「Gemini」など
    public TMP_Text bodyText; // メッセージ本文
    public Image background; // 吹き出し背景（色分け用）

    public Color userColor = new Color(0.20f, 0.45f, 0.75f, 1f); // ユーザー側の背景色
    public Color assistantColor = new Color(0.25f, 0.25f, 0.28f, 1f); // アシスタント側の背景色

    // 表示内容と色をセットする
    public void SetMessage(string speaker, string body, bool isUser)
    {
        if (speakerText != null)
        {
            speakerText.text = speaker;
        }

        if (bodyText != null)
        {
            bodyText.text = body;
        }

        if (background != null)
        {
            background.color = isUser ? userColor : assistantColor;
        }
    }
}
