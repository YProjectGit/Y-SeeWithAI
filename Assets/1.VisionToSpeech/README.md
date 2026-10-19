# 1. VisionToSpeech

カメラに映るものを **Gemini Live API** に見せ、音声で返答を受け取るアプリケーションです。

前回の [3B. SpeechToSpeechLiveAPI](https://github.com/YProjectGit/Y-TalkWithAI/tree/main/Assets/3B.SpeechToSpeechLiveAPI) と同じく、**WebSocket通信**でひとつのセッションを維持します。今回はマイクの音声に代わり、カメラの画像を送ります。

上段は **カメラ → 送信画像 → AIの返答** の3列構成です。下段に設定・接続情報、送信ログ、受信ログを表示します。

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

1. 左にカメラのプレビューが出ること、中央の接続ランプが緑の「接続済み」になることを確認してください。
2. 左下の **設定・接続情報** に、モデル・声・画像品質・送信間隔が出ていることを見てください。

### 2. Spaceで画像を送る

1. カメラに物を見せ、**Spaceキーを1回押すか、「画像を送信」ボタンを押して**ください。
2. 中央に実際に送信したJPEGと送信番号・解像度・容量・時刻が表示され、右に音声と字幕が届くことを確認してください。送信画像は次の送信まで保持されます。
3. 返答を聞いたら、見せる物や配置を変えてもう一度送ってください。

### 3. 連続送信に切り替える

1. **Stream** ボタンを押してください。**Stream** が選択色に変わり、画像が自動で送られます。
2. カメラに見せるものを変え、返答を比べてください。このモードではSpaceは使いません。
3. **シャッター** を押すと連続送信が止まり、Spaceによる送信に戻ります。

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
  "clientContent": {
    "turns": [{
      "role": "user",
      "parts": [
        {"inlineData": {"mimeType": "image/jpeg", "data": "..."}},
        {"text": "この画像を日本語で短く説明してください。"}
      ]
    }],
    "turnComplete": true
  }
}
```

1. **`clientContent.turns`** — 画像と説明の指示を、1つのユーザーターンにまとめます。
2. **`inlineData`** — JPEGの形式と、バイト列をBase64にした文字列を入れます。
3. **`turnComplete: true`** — 入力を確定し、音声の返答を求めます。

Unityでは `WebCamTexture` から画像を取得し、長辺が設定値を超える場合は縮小してから `EncodeToJPG` でJPEGにします。送る画像の大きさと品質は、Inspectorの `Max Send Long Side` と `Jpeg Quality` で指定します。

### 1回の送信とターンの区切り

**ターン**は、入力をひとまとめにして返答を求める単位です。このデモでは **Spaceを押した1回の操作で、画像と指示を同じ `clientContent` に入れて送ります。** `turnComplete: true` で入力が確定します。

音声入力用の `activityStart` / `activityEnd` は使いません。画像とテキストだけの入力をこれらで囲むと、使用モデルでは `Precondition check failed` で切断されます。また、画像とテキストを別々の `realtimeInput` で送ると、画像を認識する前に回答する場合があるため、1つのターンにまとめています。自動VADは使わず、Setupの `automaticActivityDetection.disabled` を `true` にしています。

### シャッターと連続送信

シャッターはSpaceを押したときに1ターン送ります。Streamは、その処理を自動で繰り返します。切り替える際に再接続はしません。

連続送信の間隔は初期値で約1秒ですが、前の返答の `turnComplete` を受け取り、音声再生が終わるまでは次の画像を送りません。実際の送信間隔は応答時間によって変わります。動画ファイルを送るのではなく、**その時点の静止画を繰り返し送る**構成です。

<br/>

---

## コードの解説

### VisionToSpeech（[`VisionToSpeech.cs`](Script/VisionToSpeech.cs)）

デモの本体です。接続 → 画像の取得・送信 → 受信 → 再生の順に追ってください。

通信には **ClientWebSocket** を使います。受信はバックグラウンドで行い、UIや音声再生の操作はメインスレッドのキューへ渡します。Spaceの検知には `Update` 内で旧Input Managerの `Input.GetKeyDown` を使っています。

1. **Liveセッションに接続する**
   `ConnectLiveSessionCoroutine` → `BuildSetupJson` — 接続設定を送り、`setupComplete` を待つ。
2. **送信のきっかけを作る**
   `UpdateShutterInput` / `StreamLoopCoroutine` — Space・送信ボタン、または連続送信から1ターンの処理を呼ぶ。
3. **カメラ画像を送る**
   `SendFrameTurnCoroutine` → `TryCaptureJpeg` → `SendImageTurnCoroutine` — 画像をJPEGにし、画像と指示を1つのターンとして送る。
4. **返答を振り分ける**
   `HandleServerMessage` — 音声を再生キューに入れ、文字起こしを表示する。`OnTurnComplete` で次の送信を許可する。
5. **音声を再生する**
   `PlaybackPumpCoroutine` — 受信したPCMを共通の `PcmStreamPlayer` に渡し、音声スレッドで連続再生する。

音声出力を確実に要求するため、設定と画像の指示には音声で返答する条件を付加します。文字起こしだけでターンが完了した場合は、画像を再送せず同じ回答の読み上げを1回だけ再要求します。それでも音声が届かなければエラーを表示し、再要求が20秒で完了しない場合は接続を閉じます。音声出力の条件はアプリ側で付加します。

### Dashboard（[`VisionToSpeech.Dashboard.cs`](Script/VisionToSpeech.Dashboard.cs)）

送信したJPEGをデコードして中央のプレビューに表示し、最新の返答・過去20件の履歴・音声波形・接続ランプを更新します。波形は再生中の音声データに連動します。送受信ログはスクロールでき、音声のチャンク数・受信量・最初の音声までの時間・ターン完了時間も表示します。

指示はHierarchyのVisionToSpeechコンポーネントのInspectorで編集します。`System Instruction Text` が事前指示（次回Playから反映）、`Frame Prompt Text` が画像に添える指示（次の画像送信から反映）です。永続的な変更はPlay停止中に編集し、Sceneを保存してください。画面内の2つの指示欄は確認専用です。接続中の事前指示欄は、その接続に適用済みの内容を表示します。

Inspectorの編集時と起動時に `Assets/1.VisionToSpeech/SystemInstruction.txt` と `Assets/1.VisionToSpeech/FramePrompt.txt` へ同じ文章を自動出力します。これらは確認・配布用で、直接編集しても読み込まれません。`Assets/Common/SystemInstruction.txt` はVisionToSpeechでは使いません。

レイアウトはSceneに保存されています。Editor専用の `Tools > VisionToSpeech > Rebuild Dashboard` で同じ構成を再生成できます（現在のUI配置を置き換えます）。

### 共通ライブラリ（`Assets/Common/Script/`）

上の処理を補助するライブラリです。最初はデモ本体の流れを追い、必要に応じて参照してください。

| ファイル | 中身 |
|---|---|
| [`GeminiJson`](../Common/Script/GeminiJson.cs) | JSONのエスケープ・整形・省略表示 |
| [`GeminiLiveMessages`](../Common/Script/GeminiLiveMessages.cs) | Setup・画像送信JSONの生成と、音声・字幕・完了通知の解析 |
| [`GeminiResponseTurn`](../Common/Script/GeminiResponseTurn.cs) | 返答1回分の状態と音声欠落時の再要求 |
| [`GeminiKey`](../Common/Script/GeminiKey.cs) | APIキーの読込 |
| [`PcmStreamPlayer`](../Common/Script/PcmStreamPlayer.cs) / [`PcmStreamBuffer`](../Common/Script/PcmStreamBuffer.cs) | PCMを連結して連続再生。初期値150msの先読みで受信間隔の揺れを吸収 |
| [`TextureUtil`](../Common/Script/TextureUtil.cs) | 画像の縮小 |
| [`ResponseTime`](../Common/Script/ResponseTime.cs) | 返答までの経過時間をConsoleに表示 |

共通ライブラリは他のデモも使います。自分の作品用に挙動を変える場合は、デモの `Script/` にコピーしてクラス名を変えてください。

### 共通処理の確認

通信の接続・画像取得・送信タイミングは各デモに残し、メッセージ形式と返答状態を共通化しています。表示の更新は各 `Dashboard.cs`、UI生成の基本部品は `Common/Editor/DashboardLayoutElements.cs` が担当します。

`Tools > Gemini Demos > Run Shared Checks` で、外部APIを使わずにJSON互換性、正常完了、音声0件の再要求、再要求上限、20秒タイムアウト、前の返答の待機処理が次に干渉しないことを検証できます。
