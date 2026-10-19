/// <summary>音声デモの出力形式と、音声欠落時の1回限りの再要求。</summary>
public static class GeminiAudioResponse
{
    public static string WithAudioInstruction(string instruction)
    {
        return (instruction ?? string.Empty).Trim() +
            "\nこのアプリでは返答を必ず声に出して音声で伝えてください。" +
            "プレーンテキストなどの書式指定は、話す内容の書式を意味します。文字だけで返答を終えないでください。";
    }

    public static string RetryJson(string transcript)
    {
        string prompt = "直前の返答の音声が届きませんでした。画像を再解釈せず、直前の回答をそのまま声に出して音声で伝えてください。";
        if (!string.IsNullOrWhiteSpace(transcript)) prompt += "\n読み上げる回答：\n" + transcript;
        return "{\"clientContent\":{\"turns\":[{\"role\":\"user\",\"parts\":[{\"text\":\"" +
            GeminiJson.Escape(prompt) + "\"}]}],\"turnComplete\":true}}";
    }
}
