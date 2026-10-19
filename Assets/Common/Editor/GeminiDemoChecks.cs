using System;
using System.Threading.Tasks;
using UnityEditor;

/// <summary>外部APIを使わず、共通化した境界条件を確認する。</summary>
public static class GeminiDemoChecks
{
    [MenuItem("Tools/Gemini Demos/Run Shared Checks")]
    public static void Run()
    {
        CheckMessages();
        CheckTurns();
        UnityEngine.Debug.Log("Gemini shared checks PASS: protocol, completion, retry limit, timeout, stale turn isolation");
    }

    static void Require(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    static void CheckMessages()
    {
        Require(GeminiLiveMessages.Parse("{\"setupComplete\":{}}").SetupComplete, "setupComplete");
        Require(!GeminiLiveMessages.Parse("{}").SetupComplete, "missing setupComplete");
        const string json = "{\"serverContent\":{\"turnComplete\" : \n true,\"interrupted\": false,"
            + "\"outputTranscription\":{\"text\":\"日本語\\n\\\"引用\\\"\"},"
            + "\"modelTurn\":{\"parts\":[{\"inlineData\":{\"mimeType\":\"audio/pcm;rate=24000\",\"data\":\"AQIDBA==\"}},"
            + "{\"inlineData\":{\"mimeType\":\"image/jpeg\",\"data\":\"AQIDBA==\"}},"
            + "{\"inlineData\":{\"mimeType\":\"audio/pcm\",\"data\":\"invalid!\"}}]}}}";
        var message = GeminiLiveMessages.Parse(json);
        Require(!message.SetupComplete && message.TurnComplete && !message.Interrupted, "message flags");
        Require(message.Transcript == "日本語\n\"引用\"", "transcript escaping");
        Require(message.Audio.Count == 1 && message.Audio[0].Length == 4 && message.Audio[0][3] == 4, "audio MIME/data isolation");
        Require(GeminiLiveMessages.Parse("{\"serverContent\":{\"interrupted\" : true}}").Interrupted, "interruption whitespace");
        Require(GeminiLiveMessages.Parse("{\"serverContent\":{\"outputTranscription\":{\"text\":\"audio data turnComplete\"}}}").Audio.Count == 0, "text is not audio");
        var setup = GeminiLiveMessages.Setup("model", "Kore", "", "短く");
        Require(setup == "{\"setup\":{\"model\":\"models/model\",\"realtimeInputConfig\":{\"automaticActivityDetection\":{\"disabled\":true}},\"outputAudioTranscription\":{},\"systemInstruction\":{\"parts\":[{\"text\":\"短く\"}]},\"generationConfig\":{\"responseModalities\":[\"AUDIO\"],\"speechConfig\":{\"voiceConfig\":{\"prebuiltVoiceConfig\":{\"voiceName\":\"Kore\"}}}}}}", "setup wire format changed");
        var image = GeminiLiveMessages.ImageTurn(new byte[] { 1, 2, 3 }, "画像\n\"説明\"");
        Require(image == "{\"clientContent\":{\"turns\":[{\"role\":\"user\",\"parts\":[{\"inlineData\":{\"mimeType\":\"image/jpeg\",\"data\":\"AQID\"}},{\"text\":\"画像\\n\\\"説明\\\"\"}]}],\"turnComplete\":true}}", "image wire format changed");
    }

    static void CheckTurns()
    {
        var turn = new GeminiResponseTurn();
        Require(turn.Complete() == GeminiResponseTurn.Completion.Ignored, "idle completion");
        turn.Begin();
        turn.AddAudio();
        turn.AppendTranscript("短い返答");
        Require(turn.Complete() == GeminiResponseTurn.Completion.Succeeded && !turn.IsBusy && !turn.RetryUsed, "normal completion");
        turn.Begin();
        Require(turn.AudioChunks == 0 && turn.Transcript == "", "new turn reset");
        turn.AppendTranscript("同じ回答");
        Require(turn.Complete() == GeminiResponseTurn.Completion.RetryAudio && turn.IsBusy, "missing audio recovery");
        int sends = 0, errors = 0, timeouts = 0;
        float time = 0;
        string sent = null;
        Func<string, Task> send = json => { sends++; sent = json; return Task.CompletedTask; };
        var retry = turn.RetryAudio(send, () => true, _ => { errors++; turn.Cancel(); }, () => timeouts++, () => time);
        Require(retry.MoveNext() && sends == 1 && sent.Contains("同じ回答") && !sent.Contains("inlineData"), "retry once without image");
        turn.AddAudio();
        Require(turn.Complete() == GeminiResponseTurn.Completion.Succeeded && turn.Transcript == "同じ回答", "recovery transcript fallback");
        Require(!retry.MoveNext() && errors == 0, "successful retry stop");

        turn.Begin();
        turn.Complete();
        Require(turn.Complete() == GeminiResponseTurn.Completion.Failed && !turn.IsBusy, "retry limit");
        turn.Begin();
        turn.Complete();
        retry = turn.RetryAudio(send, () => true, _ => { errors++; turn.Cancel(); }, () => timeouts++, () => time);
        Require(retry.MoveNext(), "timeout started");
        time = 21;
        Require(!retry.MoveNext() && timeouts == 1 && errors == 1, "20 second timeout");

        turn.Begin();
        turn.Complete();
        retry = turn.RetryAudio(send, () => true, _ => errors++, () => timeouts++, () => time);
        Require(retry.MoveNext(), "old retry started");
        turn.Begin();
        turn.Complete();
        time = 100;
        Require(!retry.MoveNext() && errors == 1 && timeouts == 1 && turn.IsBusy, "old retry must not cancel next turn");
        turn.Cancel();
        turn.Begin();
        turn.Complete();
        retry = turn.RetryAudio(send, () => false, _ => { errors++; turn.Cancel(); }, () => timeouts++, () => time);
        Require(!retry.MoveNext() && !turn.IsBusy && errors == 2, "disconnected retry");
    }
}
