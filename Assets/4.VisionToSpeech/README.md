# 4.VisionToSpeech

Gemini Live API に送る入力を、音声から映像へ広げます。カメラが捉えたものについて話せるようになります。

シリーズ全体の位置づけ → [Assets/Docs/demo-series-overview.md](../Docs/demo-series-overview.md)

---

## このデモで学べること

- **映像入力**  
  JPEG フレームをセッションに流し、見た内容への返答をもらう
- **送信レートと解像度**  
  映像入力の上限に合わせて、送る間隔と画像サイズを抑える

---

## 事前準備

1. Google AI Studio から Gemini の API にアクセスするための APIキーを取得し、`Assets/Common/APIKey.txt` に保管してください。  
   手順 → [Assets/Docs/gemini-ai-studio-setup.md](../Docs/gemini-ai-studio-setup.md)
2. PC にカメラがつながり、Unity から使える状態にしてください（OS のカメラ権限を含む）。
3. スピーカーまたはヘッドホンで再生音が聞こえる状態にしてください。

---

## 動かし方

Project ウィンドウで `Assets/4.VisionToSpeech/VisionToSpeech.unity` を開き、Play を押してください。

### 1. 接続とプレビューを確認する

1. 左に WebCam プレビューが出ること、上部の段階バーが `Connect` 付近であること、Status が「接続済み」になることを見てください。
2. 中央（送信）上部の **Setup** ヘッダに、`model` / `voice` / `mediaResolution` / `JPEG` などの設定行が出ることを確認してください。

### 2. Space でシャッターする

1. カメラに被写体を向け、**Space を1回押してください**（押しっぱなしではありません）。
2. 段階バーが `Send Frame` → `Receive PCM` → `Play` と進むこと、中央に `+frame …x…` のログが増えることを見てください。
3. 左に吹き出し（キャプチャと Gemini の transcription）が出て、返答が声で再生されることを確認してください。

### 3. ストリーミングに切り替える

1. 左ペインの **Stream** ボタンを押してください（**Stream ON** になります）。
2. Space が無効になり、約1秒ごとにフレームが送られることを中央ログで確認してください。
3. もう一度ボタンを押すとストリーミングが止まり、Space シャッターが再び有効になります。

### 4. 声を変えてみる

1. Play を止め、Hierarchy でデモ本体（`VisionToSpeech`）を選び、Inspector の **Voice Name**（`voiceName`）を変更してください（初期値は `Kore`）。
2. もう一度 Play を押し、中央 Setup ヘッダの `voice` が新しい名前になっていることを確認してから、Space でシャッターしてください。

声は接続時の Setup で一度だけ送るため、変更後は **Stop → Play** が必要です。  
使える声の名前一覧 → [Gemini API: Text-to-speech（Voice options）](https://ai.google.dev/gemini-api/docs/speech-generation#voices)

---

## Live API の映像入力

Live API は WebSocket で1本のセッションを張り、音声や画像をチャンクで双方向に流す仕組みです。このデモではマイクの代わりに **JPEG フレーム** を `realtimeInput.video` で送り、サーバから返る PCM を再生します。

REST で「画像理解 → TTS（Text-to-Speech）」と二段に分けるのではなく、**見たものへの返答が最初から声**になります。3B が「声の Live」なら、4 は「目の Live」です。

試し方: Space のあと中央ログに `+frame` と `activityStart` / `activityEnd` が出ること、右に `+audio` と transcription が出ることを見る。

---

## シャッターとストリーミング

**シャッター**（初期）は Space でいまのカメラ画を1枚だけ送り、ターンを閉じます。送信ログに1回分の `+frame` が増えます。

**ストリーミング**は同じ送信をおよそ1秒間隔で繰り返します（Live の映像入力は最大でおよそ 1 FPS）。ON のあいだ Space は使えません。送っているのは動画ファイルではなく、間欠的な JPEG です。送信前に長辺を 768 前後まで縮小します。

試し方: Stream を ON/OFF し、案内文と中央ログの増え方の違いを比べる。

---

## 送信／受信の2分割

3B と同じく、中央が **送信（Outbound）**、右が **受信（Inbound）** です。GenerateContent の番号欄はありません。段階バーは `Connect → Send Frame → Receive PCM → Play` です。

試し方: シャッター直後に中央が動き、返答時に右が動くのを追う。

---

## 主要クラス

### VisionToSpeech（[`VisionToSpeech.cs`](Script/VisionToSpeech.cs)）

デモの本体です。上から、接続〜フレーム送信〜受信〜再生の順に追うとわかりやすいです。

通信は **ClientWebSocket** です。受信はバックグラウンドのループで行い、UI や `AudioSource` の更新だけメインスレッドのキュー経由で戻します。Space のシャッター検知は `Update` と **旧 Input Manager**（`Input.GetKeyDown`）です。ストリーミング中は Space を無視します。

1. **Live セッションに接続する**  
   `ConnectLiveSessionCoroutine` — WSS（WebSocket Secure）接続 → Setup JSON 送信 → `setupComplete` 待ち
2. **Space シャッターで1フレーム送る**  
   `SendFrameTurnCoroutine` — `activityStart` → JPEG → 説明指示テキスト → `activityEnd`
3. **ストリーミングを切り替える**  
   `OnStreamModeButtonClicked` / `StreamLoopCoroutine` — およそ 1 FPS で同じ1ターン送信を繰り返す（再接続なし）
4. **JPEG を用意する**  
   `TryCaptureJpeg` — WebCam から取得し、長辺を 768 前後まで縮小して `EncodeToJPG`
5. **サーバメッセージを振り分ける**  
   `HandleServerMessage` — 音声は再生キュー、output transcription はログと吹き出し
6. **受信 PCM を再生する**  
   `PlaybackPumpCoroutine` — キューから `AudioClip` 化して `AudioSource.Play`

### 共通スクリプト（`Assets/Common/Script/`）

このデモが使っている共通の道具です。**上の流れを追うときに中身を読む必要はありません。**

| ファイル | 中身 |
|---|---|
| [`GeminiJson`](../Common/Script/GeminiJson.cs) | JSON のエスケープ・整形・省略表示 |
| [`GeminiJsonScan`](../Common/Script/GeminiJsonScan.cs) | Live API の受信 JSON からキーを頼りに文字列を拾う |
| [`GeminiKey`](../Common/Script/GeminiKey.cs) | APIキーの読込・マスク・generateContent の URL |
| [`AudioCodec`](../Common/Script/AudioCodec.cs) | AudioClip ⇄ WAV / PCM16 の変換 |
| [`TextureUtil`](../Common/Script/TextureUtil.cs) | テクスチャの縮小 |
| [`ChatBubble`](../Common/Script/ChatBubble.cs) | 吹き出し1件分の見た目（Prefab: [`MessageBubble.prefab`](Prefab/MessageBubble.prefab)） |

これらは他のデモも使っています。挙動を変えたくなったら Common を直さず、そのファイルをこのデモの `Script/` にコピーしてクラス名を変えてください。
