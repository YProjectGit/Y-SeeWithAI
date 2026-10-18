# 2. ScreenToSpeech

画面上に描いている絵を **Gemini Live API** に見せ、途中経過を音声で実況してもらうアプリケーションです。

[1. VisionToSpeech](../1.VisionToSpeech/README.md) の入力元を、カメラから **アプリ内の描画パッド** に替えています。描き足された画像を自動で送り、返答を音声と字幕で表示します。

<br/>

---

## 学ぶこと

- ### アプリ内の画像をAIに見せる

  アプリケーションが描いた画像を取り出し、AIの入力にする方法を学びます。

- ### 変化に応じた自動送信

  描き足されたことを検知し、操作の途中でAIから反応を受け取る仕組みを学びます。

<br/>

---

## 動かしてみる

APIキーは `Assets/Common/APIKey.txt` に設定してください。取得方法は [前回のWorkshop資料](https://scrapbox.io/y-project-cosense/UnityWorkshop_-_Gemini_API_編)を参照してください。

Projectウィンドウで `Assets/2.ScreenToSpeech/ScreenToSpeech.unity` を開き、Playしてください。

### 1. 絵を描く

1. 上部の表示が「描いてください」に変わるまで待ってください。
2. 白い紙の上をマウスでドラッグして、簡単な形を描いてください。
3. 少し待ち、絵に対する返答が音声と字幕で出ることを確認してください。

### 2. 描き足す

1. 線を足し、形を変えてください。例えば、四角に屋根や窓を描き足してみてください。
2. AIの説明が、描き途中の絵に応じてどう変わるか聞いてください。

### 3. 消して描き直す

1. 右上の **消す** を押してください。紙が白紙に戻り、字幕が消えます。
2. 別の絵を描き、新しい返答を聞いてください。

声を変えたいときは、Playを止めてInspectorの **Voice Name** を変更し、もう一度Playしてください。声は接続時に設定します。

<br/>

---

## 解説

### アプリ内の画像を入力にする

AIに見せる画像は、カメラで撮った写真に限りません。**アプリケーションが描いている画像も、同じように入力できます。**

このデモは、描画パッドが持つ `Texture2D` を使います。これは画素の色を保持するUnityの画像データです。マウスの動きに合わせて画素を書き換え、その画像を画面に表示しています。

送信時には、この `Texture2D` を必要に応じて縮小し、JPEGに変換します。PCのデスクトップ全体やUnityのウィンドウを撮る処理ではありません。

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

1のデモと同じ形式です。`data` には描画パッドのJPEGをBase64にした文字列が入ります。**画像の取得元を変えても、APIへの送信方法は共通です。**

### 描き途中の変化を送る

毎フレーム送ると、同じ絵を何度も送ったり、前の返答が来る前に次の入力が重なったりします。このデモでは、次の条件を確認してから送ります。

- **`HasInk`** — 紙に線が描かれている。
- **`IsDirty`** — 前回送信してから描き足されている。
- **`isTurnBusy` がfalse** — 前のターンが完了している。

送信すると `MarkClean` で「送信済み」にし、次に描き足されたら再び `IsDirty` がtrueになります。「消す」で白紙に戻したあとは、新たに描くまで画像を送りません。

確認の間隔は初期値で約1秒です。前の返答が完了していなければ待つため、一定速度で返答が続くとは限りません。応答を待つ間も絵を描けます。

### 実況の指示と音声の返答

画像に続いて、「この絵を実況してほしい」というテキストの指示も送ります。

```json
{
  "realtimeInput": {
    "text": "このキャンバスに描かれている絵を、日本語で短く実況してください。"
  }
}
```

1ターンの流れは、1のデモと同じ **`activityStart` → 画像 → 指示 → `activityEnd`** です。

Inspectorの **System Instruction Text** は、AIの役割や話し方を決める事前指示です。接続時のSetupで送ります。**Frame Prompt Text** は、画像を送るたびに添える指示です。

返答はPCMという音声データで届き、`AudioSource` で再生します。字幕には、Setupの `outputAudioTranscription` で有効にした返答の文字起こしを表示します。

<br/>

---

## コードの解説

### ScreenToSpeech（[`ScreenToSpeech.cs`](Script/ScreenToSpeech.cs)）

通信と実況の本体です。接続 → 描画の変化を確認 → 画像送信 → 音声・字幕の表示の順に追ってください。

通信には **ClientWebSocket** を使います。受信はバックグラウンドで行い、UIや音声再生の操作はメインスレッドのキューへ渡します。送信間隔や応答を待つ処理には **コルーチン** を使い、待っている間も描画処理を続けられるようにしています。

1. **Liveセッションに接続する**
   `ConnectLiveSessionCoroutine` → `BuildSetupJson` — 事前指示と音声の設定を送り、接続完了を待つ。
2. **画像を送るか判断する**
   `InterpretLoopCoroutine` — 描画の有無、変更の有無、前のターンの状態を確認する。
3. **描いた画像を送る**
   `SendFrameTurnCoroutine` → `TryCaptureJpeg` — 描画パッドをJPEGにし、実況の指示と一緒に送る。
4. **返答を振り分ける**
   `HandleServerMessage` — 音声を再生キューに入れ、`OnOutputTranscription` で字幕を更新する。`OnTurnComplete` で次の送信を許可する。
5. **音声を再生する**
   `PlaybackPumpCoroutine` — PCMを `AudioClip` に変換し、`AudioSource` で再生する。

### DrawingPad（[`DrawingPad.cs`](Script/DrawingPad.cs)）

マウスで線を描くための部品です。API通信は行いません。

1. **マウスの位置を受け取る**
   `OnPointerDown` / `OnDrag` — 紙の上の操作を描画位置に変換する。
2. **画像に線を描く**
   `StampLine` / `Stamp` — 画素を書き換え、描画済み・変更ありの状態を記録する。
3. **描画の状態を渡す**
   `CanvasTexture` / `HasInk` / `IsDirty` — 通信側へ画像と状態を公開する。`Clear` は白紙へ戻し、`MarkClean` は送信済みにする。

### 共通ライブラリ（`Assets/Common/Script/`）

上の処理を補助するライブラリです。最初はデモ本体の流れを追い、必要に応じて参照してください。

| ファイル | 中身 |
|---|---|
| [`GeminiJson`](../Common/Script/GeminiJson.cs) | JSONのエスケープ・整形 |
| [`GeminiJsonScan`](../Common/Script/GeminiJsonScan.cs) | 受信JSONから値を取り出す |
| [`GeminiKey`](../Common/Script/GeminiKey.cs) | APIキーの読込 |
| [`AudioCodec`](../Common/Script/AudioCodec.cs) | PCMをUnityで再生できる音声に変換 |
| [`TextureUtil`](../Common/Script/TextureUtil.cs) | 画像の縮小 |
| [`ResponseTime`](../Common/Script/ResponseTime.cs) | 返答までの経過時間をConsoleに表示 |

共通ライブラリは他のデモも使います。自分の作品用に挙動を変える場合は、デモの `Script/` にコピーしてクラス名を変えてください。
