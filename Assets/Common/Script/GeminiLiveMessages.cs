using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>両デモが使うLive APIのメッセージ形式。接続や画面には依存しない。</summary>
public static class GeminiLiveMessages
{
    public static string Setup(string modelName, string voiceName, string mediaResolution, string instruction)
    {
        StringBuilder sb = new StringBuilder(512);
        sb.Append("{\"setup\":{");
        sb.Append("\"model\":\"models/");
        sb.Append(GeminiJson.Escape(modelName));
        sb.Append("\",");

        // 音声入力は使わず、clientContent.turnComplete で画像と指示を確定する
        sb.Append("\"realtimeInputConfig\":{\"automaticActivityDetection\":{\"disabled\":true}},");
        sb.Append("\"outputAudioTranscription\":{},");

        if (!string.IsNullOrEmpty(instruction))
        {
            sb.Append("\"systemInstruction\":{\"parts\":[{\"text\":\"");
            sb.Append(GeminiJson.Escape(instruction));
            sb.Append("\"}]},");
        }

        sb.Append("\"generationConfig\":{");
        sb.Append("\"responseModalities\":[\"AUDIO\"],");
        if (!string.IsNullOrEmpty(mediaResolution))
        {
            // GenerationConfig のフィールド。setup 直下に置くとサーバが切断する
            sb.Append("\"mediaResolution\":\"");
            sb.Append(GeminiJson.Escape(mediaResolution));
            sb.Append("\",");
        }

        sb.Append("\"speechConfig\":{\"voiceConfig\":{\"prebuiltVoiceConfig\":{\"voiceName\":\"");
        sb.Append(GeminiJson.Escape(voiceName));
        sb.Append("\"}}}");
        sb.Append('}'); // generationConfig
        sb.Append("}}"); // setup + root
        return sb.ToString();
    }

    public static string ImageTurn(byte[] jpeg, string prompt)
    {
        return "{\"clientContent\":{\"turns\":[{\"role\":\"user\",\"parts\":["
            + "{\"inlineData\":{\"mimeType\":\"image/jpeg\",\"data\":\""
            + Convert.ToBase64String(jpeg)
            + "\"}},{\"text\":\"" + GeminiJson.Escape(prompt)
            + "\"}]}],\"turnComplete\":true}}";
    }

    public sealed class ServerMessage
    {
        public bool SetupComplete, TurnComplete, Interrupted;
        public string Transcript;
        public readonly List<byte[]> Audio = new List<byte[]>();
    }

    // 対象のオブジェクトとフィールドを読む。空白・改行・キー順に依存しない。
    public static ServerMessage Parse(string json)
    {
        var result = new ServerMessage();
        if (string.IsNullOrEmpty(json))
            return result;
        var envelope = JsonUtility.FromJson<Envelope>(json);
        // 空オブジェクトの有無は、JsonUtilityが欠落フィールドも生成するため別に判定する。
        result.SetupComplete = GeminiJsonScan.HasRootObject(json, "setupComplete");
        var content = envelope.serverContent;
        if (content == null)
            return result;
        result.TurnComplete = content.turnComplete;
        result.Interrupted = content.interrupted;
        result.Transcript = content.outputTranscription?.text;
        if (content.modelTurn?.parts == null)
            return result;
        foreach (var part in content.modelTurn.parts)
        {
            if (part == null)
                continue;
            var data = !string.IsNullOrEmpty(part.inlineData?.data) ? part.inlineData : part.inline_data;
            var mime = !string.IsNullOrEmpty(data?.mimeType) ? data.mimeType : data?.mime_type;
            if (mime == null || !mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrEmpty(data.data))
                continue;
            try
            {
                var pcm = Convert.FromBase64String(data.data);
                if (pcm.Length >= 2)
                    result.Audio.Add(pcm);
            }
            catch (FormatException) { /* 壊れた音声データだけを無視する。 */ }
        }
        return result;
    }

#pragma warning disable 0649 // JsonUtilityが受信JSONから設定するDTO。
    [Serializable]
    sealed class Envelope
    {
        public Content serverContent;
    }
    [Serializable]
    sealed class Content
    {
        public bool turnComplete, interrupted;
        public Transcription outputTranscription;
        public ModelTurn modelTurn;
    }
    [Serializable]
    sealed class Transcription
    {
        public string text;
    }
    [Serializable]
    sealed class ModelTurn
    {
        public Part[] parts;
    }
    [Serializable]
    sealed class Part
    {
        public InlineData inlineData, inline_data;
    }
    [Serializable]
    sealed class InlineData
    {
        public string mimeType, mime_type, data;
    }
#pragma warning restore 0649
}
