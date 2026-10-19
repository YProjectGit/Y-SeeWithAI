using System;
using System.Collections;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>画像1枚への返答状態。メインスレッドでのみ更新する。</summary>
public sealed class GeminiResponseTurn
{
    public enum Completion
    {
        Ignored, RetryAudio, Failed, Succeeded
    }
    public const string MissingAudioError = "音声を受信できませんでした（音声の再要求も完了）。もう一度送信してください。";
    public bool IsBusy { get; private set; }
    public bool RetryUsed { get; private set; }
    public int AudioChunks { get; private set; }
    public string Transcript { get; private set; } = string.Empty;
    public int Version { get; private set; }
    string retryTranscript;

    public void Begin()
    {
        Version++;
        IsBusy = true;
        RetryUsed = false;
        AudioChunks = 0;
        Transcript = string.Empty;
        retryTranscript = null;
    }
    public void AddAudio()
    {
        AudioChunks++;
    }
    public void AppendTranscript(string fragment)
    {
        Transcript += fragment;
    }
    public void Cancel()
    {
        IsBusy = false;
    }

    public Completion Complete()
    {
        if (!IsBusy)
            return Completion.Ignored;
        if (AudioChunks == 0)
        {
            if (RetryUsed)
            {
                IsBusy = false;
                return Completion.Failed;
            }
            RetryUsed = true;
            retryTranscript = Transcript;
            Transcript = string.Empty;
            return Completion.RetryAudio;
        }
        IsBusy = false;
        if (string.IsNullOrEmpty(Transcript))
            Transcript = retryTranscript ?? string.Empty;
        return Completion.Succeeded;
    }

    // 1回だけ再送し、応答完了を待つ。画像やUIの扱いは各デモに残す。
    // Versionにより、古い待機処理が次の画像の応答へ干渉しない。
    public IEnumerator RetryAudio(Func<string, Task> send, Func<bool> connected,
        Action<string> fail, Action timedOut, Func<float> now = null)
    {
        now = now ?? (() => Time.realtimeSinceStartup);
        int version = Version;
        if (!connected())
        {
            fail("音声の再要求に失敗しました（未接続）。");
            yield break;
        }
        Task task;
        try
        {
            task = send(GeminiAudioResponse.RetryJson(retryTranscript));
        }
        catch (Exception)
        {
            fail("音声の再要求に失敗しました。");
            yield break;
        }
        while (!task.IsCompleted && Version == version && IsBusy)
            yield return null;
        if (Version != version || !IsBusy)
            yield break;
        if (task.IsFaulted || task.IsCanceled)
        {
            fail("音声の再要求に失敗しました。");
            yield break;
        }
        float deadline = now() + 20f;
        while (Version == version && IsBusy && now() < deadline)
            yield return null;
        if (Version == version && IsBusy)
        {
            timedOut();
            fail("音声の再要求がタイムアウトしました。再接続してください。");
        }
    }
}
