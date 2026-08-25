# 5.ScreenToSpeech

映像の入力元を、カメラからアプリ自身が描く画面に替えます。アプリの中で起きていることを、そのまま AI に見せられます。

シリーズ全体の位置づけ → [Assets/Docs/demo-series-overview.md](../Docs/demo-series-overview.md)

---

## このデモで学べること

- **画面キャプチャ**  
  カメラではなく、アプリが描いている画像を入力にする

---

## 事前準備

1. Google AI Studio から Gemini の API にアクセスするための APIキーを取得し、`Assets/Common/APIKey.txt` に保管してください。  
   手順 → [Assets/Docs/gemini-ai-studio-setup.md](../Docs/gemini-ai-studio-setup.md)
2. スピーカーまたはヘッドホンで再生音が聞こえる状態にしてください。

入力は画面に描いた絵だけなので、マイクとカメラは使いません。

---

## 動かし方

Project ウィンドウで `Assets/5.ScreenToSpeech/ScreenToSpeech.unity` を開き、Play を押してください。

### 1. 接続を待つ

1. 画面中央に白い紙が出ること、上部の状態が「接続中…」から「描いてください」に変わることを見てください。

### 2. 描いて、声と字幕を聞く

1. 白い紙の上をマウスでドラッグして、なにか描いてください。
2. 少し待つと、いまの絵への解釈が声で再生され、下部に同じ内容の字幕が出ることを確認してください。
3. 線を足して、解釈が絵の変化に追いつくかを聞いてください。

### 3. 消して、もう一度描く

1. 右上の **消す** を押してください。紙が白紙に戻り、字幕も消えます。
2. 別の形を描き、新しい解釈が声で返ることを確認してください。

### 4. 声を変えてみる

1. Play を止め、Hierarchy でデモ本体（`ScreenToSpeech`）を選び、Inspector の **Voice Name**（`voiceName`）を変更してください（初期値は `Kore`）。
2. もう一度 Play を押し、描いて声の違いを聞いてください。

声は接続時の Setup で一度だけ送るため、変更後は **Stop → Play** が必要です。  
使える声の名前一覧 → [Gemini API: Text-to-speech（Voice options）](https://ai.google.dev/gemini-api/docs/speech-generation#voices)

---

## 画面キャプチャ

一般に画面キャプチャとは、カメラではなく **いま画面に出ている絵** を画像として取ることです。このデモでは OS のデスクトップ全体ではなく、アプリ内の **ドローイングパッド**（白い紙の Texture2D）を撮っています。

4.VisionToSpeech が外の世界（WebCam）を見るのに対し、ここでは **自分で描いた線** が入力です。撮った画像は JPEG にして、4 と同じ Live API の `realtimeInput.video` で送ります。

試し方: 家や顔など、形が分かりやすいものを描き、声と字幕が紙の内容に触れているかを聞く。

---

## 描きながらの解釈

描き終わってボタンを押すのではなく、**線が増えるたびに、いまの紙を見て声で返す** やり方です。送信の間隔はおよそ1秒。前の返答が終わるまで次は送らず、白紙のままなら何も送りません。

新しい解釈が始まるときは、前の声を止めてから話します。描き足すと「線」→「四角」→「家」のように、途中経過への実況が聞こえることがあります。

試し方: 簡単な形から描き始め、線を足すたびに声がどう変わるかを聞く。**消す** のあとは、次に描くまで声は出ません。

---

## 送信フレーム

送っているのは動画ファイルではなく、その瞬間の紙を写した **JPEG の静止画** です。Live の映像入力は最大でおよそ 1 FPS なので、連続した動画ではなく間欠的なスナップショットになります。送信前に長辺を 768 前後まで縮小します。

4 のシャッター／ストリームトグルは置いていません。描くこと自体が送信のきっかけです。

試し方: ゆっくり線を足し、解釈が「いま見えている紙」に対して返ってくることを確認する。

---

## 主要クラス

### ScreenToSpeech（[`ScreenToSpeech.cs`](Script/ScreenToSpeech.cs)）

デモの本体です。上から、接続〜自動送信〜受信〜再生の順に追うとわかりやすいです。

通信は **ClientWebSocket** です。受信はバックグラウンドのループで行い、UI や `AudioSource` の更新だけメインスレッドのキュー経由で戻します。コルーチンは `Update` などのメインスレッド処理とは独立した時間軸で進むので、応答待ちのあいだも描き続けられます。

1. **Live セッションに接続する**  
   `ConnectLiveSessionCoroutine` — WSS（WebSocket Secure）接続 → Setup JSON 送信 → `setupComplete` 待ち
2. **描きながらフレームを送る**  
   `InterpretLoopCoroutine` / `SendFrameTurnCoroutine` — 描き足された紙をおよそ 1 FPS で `activityStart` → JPEG → 実況指示 → `activityEnd`
3. **JPEG を用意する**  
   `TryCaptureJpeg` — キャンバスの Texture2D を長辺 768 前後まで縮小して `EncodeToJPG`
4. **サーバメッセージを振り分ける**  
   `HandleServerMessage` — 音声は再生キュー、output transcription は字幕
5. **受信 PCM を再生する**  
   `PlaybackPumpCoroutine` — キューから `AudioClip` 化して `AudioSource.Play`

### DrawingPad（[`DrawingPad.cs`](Script/DrawingPad.cs)）

白い紙にマウスで線を描く部品です。通信は持ちません。`HasInk` と `IsDirty` だけを `ScreenToSpeech` に渡し、空の紙を送らない判断に使います。

### 共通スクリプト（`Assets/Common/Script/`）

このデモが使っている共通の道具です。どれも入力と出力だけの小さな関数なので、**上の流れを追うときに中身を読む必要はありません。**

| ファイル | 中身 |
|---|---|
| [`GeminiJson`](../Common/Script/GeminiJson.cs) | JSON のエスケープ・整形・省略表示 |
| [`GeminiJsonScan`](../Common/Script/GeminiJsonScan.cs) | Live API の受信 JSON からキーを頼りに文字列を拾う |
| [`GeminiKey`](../Common/Script/GeminiKey.cs) | APIキーの読込・マスク・generateContent の URL |
| [`AudioCodec`](../Common/Script/AudioCodec.cs) | AudioClip ⇄ WAV / PCM16 の変換 |
| [`TextureUtil`](../Common/Script/TextureUtil.cs) | テクスチャの縮小 |

これらは他のデモも使っています。挙動を変えたくなったら Common を直さず、そのファイルをこのデモの `Script/` にコピーしてクラス名を変えてください。
