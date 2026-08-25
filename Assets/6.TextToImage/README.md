# 6.TextToImage

返事をテキストや音声ではなく、画像として受け取ります。Gemini の画像生成モデルで、言葉から絵を作れます。

シリーズ全体の位置づけ → [Assets/Docs/demo-series-overview.md](../Docs/demo-series-overview.md)

---

## このデモで学べること

- **画像生成**  
  テキスト（プロンプト）から画像を作る
- **responseModalities**  
  テキストではなく画像を返すよう、リクエストで指定する
- **inlineData**  
  Base64 の画像データを取り出して、画像に戻す

---

## 事前準備

Google AI Studio から Gemini の API にアクセスするための APIキーを取得し、`Assets/Common/APIKey.txt` に保管してください。  
手順 → [Assets/Docs/gemini-ai-studio-setup.md](../Docs/gemini-ai-studio-setup.md)  
無料枠で 429 が出たら、有料への移り方と値段の目安 → [Assets/Docs/gemini-api-pricing.md](../Docs/gemini-api-pricing.md)

このデモは画像生成モデル（既定は `gemini-3.1-flash-image`）を使います。無料枠も課金も、テキスト用の Lite とは別に数えます。

---

## 動かし方

Project ウィンドウで `Assets/6.TextToImage/TextToImage.unity` を開き、Play を押してください。

### 1. プロンプトから絵を作る

1. 左ペイン下部に、たとえば「赤い自転車が公園に停まっている、昼の写真」と入れて **送信** を押してください（Enter でも送信できます。Shift+Enter で改行です）。
2. 左に生成画像、中央に Request、右に Response が出ることを確認してください。
3. 別のプロンプトをもう一度送り、左の画像が上書きされることを見てください。

### 2. Request で画像を返す設定を見る

1. 中央ペインの Request を開き、`generationConfig` の `"responseModalities"` に `"IMAGE"` が入っていることを確認してください。
2. 同じ欄に `imageConfig`（`aspectRatio` / `imageSize`）があることも見てください。
3. `x-goog-api-key` がマスクされていることを確認してください。

### 3. Response で画像バイトを追う

1. 右ペインの Response 先頭に、`HTTP 200` と `mimeType` / `image bytes` が出ることを確認してください。
2. JSON 本文の `inlineData.data` が先頭だけになっていること（長い Base64 は省略）を見てください。
3. 左の絵が、そのバイト列を `Texture2D` にしたものだと対応づけてください。

---

## 画像生成

テキストの説明（プロンプト）から、新しい画像を作ることです。1A のチャットが「文を返す」なら、ここでは同じ `generateContent` で **絵を返して** もらいます。

たとえ話で言うと、手紙の返事が作文ではなく **絵葉書** になるイメージです。送る側はこれまでどおり JSON ですが、返ってくる `parts` にテキストだけでなく、画像のバイト列（`inlineData`）が入ります。

試し方: 左の絵と、中央 Request のプロンプト、右 Response の `mimeType` を見比べる。

---

## responseModalities

Gemini に「何の形で返してほしいか」を伝える設定です。1A では指定を省略しているので、テキストが返ります。このデモでは `generationConfig.responseModalities` に `"TEXT"` と `"IMAGE"` を載せ、画像を必須にします。

テキスト part があれば、左の画像の下に短いキャプションとして出ます。会話の履歴は送りません。毎回、いま打ったプロンプトだけです。

試し方: 中央 Request の `responseModalities` を見る。縦横比や解像度を変えたいときは、Play を止めて Inspector の `aspectRatio` / `imageSize` を変更する。

---

## inlineData（画像バイト）

JSON の中に、画像そのものを Base64 という文字の列で埋め込んだものです。3A の TTS（Text-to-Speech）が音声バイトを `inlineData` で受け取ったのと同じ入れ物で、中身が絵になっています。

Unity ではその文字をバイト列に戻し、`Texture2D.LoadImage` で画像にして `RawImage` に載せます。右ペインでは長い Base64 を省略し、先頭とバイト数だけを出します（送受信そのものは全文です）。

試し方: 右の `image bytes` と、省略された `data` の `chars total` を見る。左に絵が出ていることと対応させる。

---

## 主要クラス

### TextToImage（[`TextToImage.cs`](Script/TextToImage.cs)）

デモの本体です。上から、送信後の流れを追うとわかりやすいです。

通信は **UnityWebRequest**（HTTP の送受信）と **コルーチン**（`IEnumerator` + `yield`）による **非同期処理** です。コルーチンは `Update` などのメインスレッドの処理とは独立した時間軸で進むので、応答待ちのあいだも画面が固まりません。

1. **起動時の準備をする**  
   `Start` — APIキー読込、シーンの UI、送信ボタン／Enter の購読
2. **送信を始める**  
   `OnSendClicked` → `StartCoroutine(SendImageCoroutine)` — 送信の入口
3. **API と通信する**  
   `SendImageCoroutine` — 通信本体  
   - `BuildRequestJson` → `UnityWebRequest` で POST  
   - `yield return request.SendWebRequest()` で応答待ち  
   - Response 表示 → 画像抽出 → `Texture2D` 表示
4. **リクエスト JSON を組み立てる**  
   `BuildRequestJson` — いまのプロンプト1件と `responseModalities` / `imageConfig` を載せる
5. **画像バイトを取り出す**  
   `TryExtractInlineImage` — レスポンス JSON から `inlineData` とテキスト part を取り出す
6. **送受信を画面に出す**  
   `ShowRequest` / `ShowResponse` — 中央・右ペインへの可視化（Base64 は表示だけ短縮）

### 共通スクリプト（`Assets/Common/Script/`）

このデモが使っている共通の道具です。どれも入力と出力だけの小さな関数なので、**上の流れを追うときに中身を読む必要はありません。**

| ファイル | 中身 |
|---|---|
| [`GeminiJson`](../Common/Script/GeminiJson.cs) | JSON のエスケープ・整形・省略表示 |
| [`GeminiKey`](../Common/Script/GeminiKey.cs) | APIキーの読込・マスク・generateContent の URL |

これらは他のデモも使っています。挙動を変えたくなったら Common を直さず、そのファイルをこのデモの `Script/` にコピーしてクラス名を変えてください。
