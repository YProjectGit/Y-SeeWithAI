# 1. VisionToSpeech

カメラに映るものを **Gemini Live API** に見せ、音声で返答を受け取るアプリケーションです。

前回の [3B. SpeechToSpeechLiveAPI](https://github.com/YProjectGit/Y-TalkWithAI/tree/main/Assets/3B.SpeechToSpeechLiveAPI) と同じく、**WebSocket通信**でひとつのセッションを維持します。今回はマイクの音声に代わり、カメラの画像を送ります。

学習のため、中央に送信内容、右に受信した音声と文字起こしが表示されます。

<br/>

---

## 学ぶこと

- ### Live APIへの画像入力

  カメラから取得した画像を送り、見た内容について音声で返答を受け取る方法を学びます。

- ### シャッターと連続送信

  操作した瞬間に1枚送る方法と、間隔を空けて画像を繰り返し送る方法を学びます。

<br/>

---

## 動かしてみる

APIキーは `Assets/Common/APIKey.txt` に設定してください。取得方法は [前回のWorkshop資料](https://scrapbox.io/y-project-cosense/UnityWorkshop_-_Gemini_API_編)を参照してください。

Projectウィンドウで `Assets/1.VisionToSpeech/VisionToSpeech.unity` を開き、Playしてください。

### 1. 接続を確認する

1. 左にカメラのプレビューが出ること、Statusが接続済みになることを確認してください。
2. 中央上部の **Setup** に、モデルや声などの接続設定が出ていることを見てください。

### 2. Spaceで画像を送る

1. カメラに物を見せ、**Spaceキーを1回押して**ください。
2. 中央の送信ログに `+frame` が増え、右に音声と文字起こしが届くことを確認してください。
3. 返答を聞いたら、見せる物や配置を変えてもう一度送ってください。

### 3. 連続送信に切り替える

1. **Stream** ボタンを押してください。ボタンが **Stream ON** に変わり、画像が自動で送られます。
2. カメラに見せるものを変え、返答を比べてください。このモードではSpaceは使いません。
3. もう一度ボタンを押すと連続送信が止まり、Spaceによる送信に戻ります。

声を変えたいときは、Playを止めてInspectorの **Voice Name** を変更し、もう一度Playしてください。声は接続時に設定します。

<br/>

---

## 解説

### Live APIへの画像入力

Live APIは、WebSocketで接続を保ち、そのセッション内で入力と返答をやり取りする仕組みです。このデモでは **JPEG画像を送り、PCMという音声データとtranscription（文字起こし）を受け取ります。**

接続時のSetupでは、`responseModalities` に `AUDIO` を指定し、`outputAudioTranscription` で返答の文字起こしを有効にします。画像の解釈と音声の生成を、同じセッションで行います。

**画像を送るJSON**

```json
{
  "realtimeInput": {
    "video": {
      "mimeType": "image/jpeg",
      "data": "..."
    }
  }
}
```

1. **`realtimeInput.video`** — セッション中に送る画像です。名前はvideoですが、ここに載せるのは1枚のJPEGです。
2. **`mimeType`** — 画像の形式を指定します。
3. **`data`** — JPEGのバイト列をBase64という文字列に変換したものです。

Unityでは `WebCamTexture` から画像を取得し、長辺が設定値を超える場合は縮小してから `EncodeToJPG` でJPEGにします。送る画像の大きさと品質は、Inspectorの `Max Send Long Side` と `Jpeg Quality` で指定します。

### 1回の送信とターンの区切り

**ターン**は、入力をひとまとめにして返答を求める単位です。このデモは次の順でメッセージを送ります。

1. `activityStart` — 入力の開始を伝える。
2. `video` — カメラ画像を送る。
3. `text` — 画像を説明してほしいという指示を送る。
4. `activityEnd` — 入力の終了を伝える。

```json
{"realtimeInput":{"activityStart":{}}}
```

画像に続いて、`realtimeInput.text` で説明の指示を送ります。最後に次のメッセージで入力を閉じます。

```json
{"realtimeInput":{"activityEnd":{}}}
```

前回の音声デモではSpaceの押し離しで区切りましたが、今回は **Spaceを押した1回の操作で、画像と指示をまとめて送ります。** 自動VADは使わず、Setupの `automaticActivityDetection.disabled` を `true` にしています。

### シャッターと連続送信

シャッターはSpaceを押したときに1ターン送ります。Streamは、その処理を自動で繰り返します。切り替える際に再接続はしません。

連続送信の間隔は初期値で約1秒ですが、前の返答の `turnComplete` を受け取るまでは次の画像を送りません。実際の送信間隔は応答時間によって変わります。動画ファイルを送るのではなく、**その時点の静止画を繰り返し送る**構成です。

<br/>

---

## コードの解説

### VisionToSpeech（[`VisionToSpeech.cs`](Script/VisionToSpeech.cs)）

デモの本体です。接続 → 画像の取得・送信 → 受信 → 再生の順に追ってください。

通信には **ClientWebSocket** を使います。受信はバックグラウンドで行い、UIや音声再生の操作はメインスレッドのキューへ渡します。Spaceの検知には `Update` 内で旧Input Managerの `Input.GetKeyDown` を使っています。

1. **Liveセッションに接続する**
   `ConnectLiveSessionCoroutine` → `BuildSetupJson` — 接続設定を送り、`setupComplete` を待つ。
2. **送信のきっかけを作る**
   `UpdateShutterInput` / `StreamLoopCoroutine` — Space、または連続送信から1ターンの処理を呼ぶ。
3. **カメラ画像を送る**
   `SendFrameTurnCoroutine` → `TryCaptureJpeg` — 画像をJPEGにし、開始・画像・指示・終了を順に送る。
4. **返答を振り分ける**
   `HandleServerMessage` — 音声を再生キューに入れ、文字起こしを表示する。`OnTurnComplete` で次の送信を許可する。
5. **音声を再生する**
   `PlaybackPumpCoroutine` — 受信したPCMを `AudioClip` に変換し、`AudioSource` で再生する。

### 共通ライブラリ（`Assets/Common/Script/`）

上の処理を補助するライブラリです。最初はデモ本体の流れを追い、必要に応じて参照してください。

| ファイル | 中身 |
|---|---|
| [`GeminiJson`](../Common/Script/GeminiJson.cs) | JSONのエスケープ・整形・省略表示 |
| [`GeminiJsonScan`](../Common/Script/GeminiJsonScan.cs) | 受信JSONから値を取り出す |
| [`GeminiKey`](../Common/Script/GeminiKey.cs) | APIキーの読込 |
| [`AudioCodec`](../Common/Script/AudioCodec.cs) | PCMをUnityで再生できる音声に変換 |
| [`TextureUtil`](../Common/Script/TextureUtil.cs) | 画像の縮小 |
| [`ChatBubble`](../Common/Script/ChatBubble.cs) | 吹き出しの表示 |
| [`ResponseTime`](../Common/Script/ResponseTime.cs) | 返答までの経過時間をConsoleに表示 |

共通ライブラリは他のデモも使います。自分の作品用に挙動を変える場合は、デモの `Script/` にコピーしてクラス名を変えてください。
