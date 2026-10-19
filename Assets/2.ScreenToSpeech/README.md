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

画面左側は描画と小さな返答字幕、右側は実際に送ったJPEG・送受信ログ・設定要約です。ログには送信番号、容量、音声開始までの時間、音声受信量、完了時間を表示します。描画後も送信画像は次の送信まで保持されます。

返答は初期設定で **日本語40文字以内・1文** をAIに指示します。Inspectorの **Response Character Limit** で調整できます。生成への指示なので厳密な文字数を保証するものではなく、音声の途中切断は行いません。

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
  "clientContent": {
    "turns": [{
      "role": "user",
      "parts": [
        {"inlineData": {"mimeType": "image/jpeg", "data": "..."}},
        {"text": "このキャンバスに描かれている絵を、日本語で短く実況してください。"}
      ]
    }],
    "turnComplete": true
  }
}
```

1. **`clientContent.turns`** — 画像と実況の指示を、1つのユーザーターンにまとめます。
2. **`inlineData`** — JPEGの形式と、バイト列をBase64にした文字列を入れます。
3. **`turnComplete: true`** — 入力を確定し、音声の返答を求めます。

1のデモと同じ形式です。`data` には描画パッドのJPEGをBase64にした文字列が入ります。**画像の取得元を変えても、APIへの送信方法は共通です。**

### 描き途中の変化を送る

毎フレーム送ると、同じ絵を何度も送ったり、前の返答が来る前に次の入力が重なったりします。このデモでは、次の条件を確認してから送ります。

- **`HasInk`** — 紙に線が描かれている。
- **`IsDirty`** — 前回送信してから描き足されている。
- **`isTurnBusy` がfalse** — 前のターンが完了している。

送信すると `MarkClean` で「送信済み」にし、次に描き足されたら再び `IsDirty` がtrueになります。「消す」で白紙に戻したあとは、新たに描くまで画像を送りません。

確認の間隔は初期値で約1秒です。前の返答が完了していなければ待つため、一定速度で返答が続くとは限りません。応答を待つ間も絵を描けます。

### 実況の指示と音声の返答

画像と「この絵を実況してほしい」という指示を、同じ `clientContent.turns[0].parts` に入れて送ります。`turnComplete: true` で返答を求めます。

音声入力用の `activityStart` / `activityEnd` は使いません。1のデモと同じく、画像とテキストだけの入力で起きる切断と、画像の認識前に回答する競合を避けるため、1つのターンにまとめています。

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
   `PlaybackPumpCoroutine` — 受信したPCMを共通の `PcmStreamPlayer` に渡し、音声スレッドで連続再生する。

音声出力を確実に要求するため、設定と画像の指示には音声で返答する条件を付加します。文字起こしだけでターンが完了した場合は、画像を再送せず同じ回答の読み上げを1回だけ再要求します。それでも音声が届かなければエラーを表示し、再要求が20秒で完了しない場合は接続を閉じます。共通の指示ファイル自体は変更しません。

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
| [`GeminiLiveMessages`](../Common/Script/GeminiLiveMessages.cs) | Setup・画像送信JSONの生成と、音声・字幕・完了通知の解析 |
| [`GeminiResponseTurn`](../Common/Script/GeminiResponseTurn.cs) | 返答1回分の状態と音声欠落時の再要求 |
| [`GeminiKey`](../Common/Script/GeminiKey.cs) | APIキーの読込 |
| [`PcmStreamPlayer`](../Common/Script/PcmStreamPlayer.cs) / [`PcmStreamBuffer`](../Common/Script/PcmStreamBuffer.cs) | PCMを連結して連続再生。初期値150msの先読みで受信間隔の揺れを吸収 |
| [`TextureUtil`](../Common/Script/TextureUtil.cs) | 画像の縮小 |
| [`ResponseTime`](../Common/Script/ResponseTime.cs) | 返答までの経過時間をConsoleに表示 |

共通ライブラリは他のデモも使います。自分の作品用に挙動を変える場合は、デモの `Script/` にコピーしてクラス名を変えてください。

### ダッシュボードの構成

`ScreenToSpeech.Dashboard.cs` が送信JPEG、簡潔なログ、音声波形を更新します。Sceneの配置は `Tools > ScreenToSpeech > Rebuild Dashboard` で再生成できます（現在のUI配置を置き換えます）。描画テクスチャは横長の表示に合わせて1152×768、標準の送信画像は768×512です。

### 共通処理の確認

通信の接続・画像取得・送信タイミングは各デモに残し、メッセージ形式と返答状態を共通化しています。表示の更新は各 `Dashboard.cs`、UI生成の基本部品は `Common/Editor/DashboardLayoutElements.cs` が担当します。

`Tools > Gemini Demos > Run Shared Checks` で、外部APIを使わずにJSON互換性、正常完了、音声0件の再要求、再要求上限、20秒タイムアウト、前の返答の待機処理が次に干渉しないことを検証できます。
