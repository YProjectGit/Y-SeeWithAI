# Google AI Studio セットアップ手順

音声・画像インタラクション・ワークショップ用 / Gemini Developer API を Unity から使うための準備  
（最終確認: 2026年8月）

---

## やること

**APIキーを1本取得し、疎通確認する**（所要10〜15分）。

1. Google AI Studio にログインする
2. APIキーをつくる
3. キーを保管する
4. キーが動くか確認する

> 使うのは **Google AI Studio だけ**です。Google Cloud Console / Vertex AI / gcloud は使いません。

必要なもの: Google アカウント、ブラウザ。最初はクレジットカード不要です。無料枠の回数に達したら [gemini-api-pricing.md](gemini-api-pricing.md) を見て有料に移ってください。

---

## ステップ1: ログイン

1. [`https://aistudio.google.com`](https://aistudio.google.com) を開く
2. Google アカウントでログインする
3. 初回は利用規約に同意する

---

## ステップ2: APIキーをつくる

1. 左サイドバー下部の **鍵アイコンのボタン（Get API Key）** を開く  
   （直リンク: [`https://aistudio.google.com/app/apikey`](https://aistudio.google.com/app/apikey)）
2. **Create API key** を押す
3. 初めてなら **Create API key in new project** を選ぶ
4. 発行されたキーを **Copy** する

> **この場で新しく作ってください。** 古いキーの使い回しは避けます。  
> 今作ると Auth キー（多くは `AQ.` 始まり）になります。`AIza...`（Standard）は不要です。一覧の Key Type が `Auth` なら OK です。

---

## ステップ3: キーを保管する

1. `Assets/Common/APIKey.txt` の `Paste Your APIKey Here…` を、自分のキー1行に置き換える（前後の空白に注意）
2. 本物のキーはコミットしない

キーを他人に見せないこと（SNS・チャット・スクリーンショット・公開リポジトリ禁止）。漏らしたら AI Studio で削除し、作り直す。

このワークショップでは、キーを Unity クライアント（`Assets/Common/APIKey.txt`）から直接送ります。学習用の最短経路です。

本番のアプリではこの形にしません。キーは端末にも配布ビルドにも入れず、自前のサーバが短い寿命の仮の資格情報（ephemeral token）を発行し、クライアントはそのトークンだけを Gemini に渡す形が推奨されます。

---

## ステップ4: キーが動くか確認する

Unity に進む前に必ず確認します。モデルは **`gemini-3.1-flash-lite`** を使います。

### Cursor などの AI Agent に任せる

ステップ3まで終わっていれば、Agent に疎通確認を頼めます。チャットで次のように依頼してください。

```
Assets/Common/APIKey.txt のキーが有効か、gemini-3.1-flash-lite で疎通確認して。
```

Agent が API を呼び、日本語などの応答が返れば **成功** です。失敗したら理由（401 / 429 / 404 など）も聞いてください。

### Windows（PowerShell）

シェル上でキーを直編集しないでください。**メモ帳で完成させてから**貼ります。

1. **メモ帳**を開く（`Windows` キー → `メモ帳` と入力）
2. 下のコードをコピーして、メモ帳に貼る
3. メモ帳上で `ここにキーを貼る` を、自分の API キーに置き換える（前後の `"` は消さない）
4. メモ帳の内容をすべて選択してコピー（`Ctrl` + `A` → `Ctrl` + `C`）
5. `Windows` キー → `powershell` と入力し、**Windows PowerShell** を開く
6. PowerShell に貼り付け（右クリック、または `Ctrl` + `V`）して `Enter`

```powershell
$apiKey = "ここにキーを貼る"
$uri = "https://generativelanguage.googleapis.com/v1beta/models/gemini-3.1-flash-lite:generateContent"
$body = '{"contents":[{"parts":[{"text":"Say hi in one word."}]}]}'
Invoke-RestMethod -Uri $uri -Method Post -ContentType "application/json; charset=utf-8" -Headers @{ "x-goog-api-key" = $apiKey } -Body $body
```

`candidates` の中に返答テキスト（例: `Hi`）があれば **成功** です。

### Mac（ターミナル）

ターミナル上でキーを直編集しないでください。**テキストエディットで完成させてから**貼ります。

1. **テキストエディット**を開く（`Command` + `Space` → `テキストエディット`）
2. メニュー **フォーマット → 標準テキストにする**（コードが装飾されないようにする）
3. 下のコマンドをコピーして、テキストエディットに貼る
4. テキストエディット上で `ここにキーを貼る` を、自分の API キーに置き換える
5. 内容をすべて選択してコピー（`Command` + `A` → `Command` + `C`）
6. `Command` + `Space` → `ターミナル` でターミナルを開く
7. ターミナルに貼り付け（`Command` + `V`）して `Enter`

```bash
curl -s "https://generativelanguage.googleapis.com/v1beta/models/gemini-3.1-flash-lite:generateContent" -H "x-goog-api-key: ここにキーを貼る" -H "Content-Type: application/json" -X POST -d '{"contents":[{"parts":[{"text":"Say hi in one word."}]}]}'
```

返答テキストを含む JSON が返れば **成功** です。

コマンドの実行に不安がある場合は、左メニューの **Playground** で話しかけて返事が返るか確認しても構いません（キー疎通を確実に確かめられるのは上のコマンドです）。

---

## つまずいたとき

| 症状 | 確認 |
|---|---|
| `401` / `403` | キーの空白・改行。Key Type が `Auth` か。新しく作り直す |
| `429` | 無料枠上限。待つだけでは日次回数は回復しないことが多い。→ [gemini-api-pricing.md](gemini-api-pricing.md) |
| `404` | URL / モデル名が古くないか（`gemini-3.1-flash-lite`） |
| 応答が空 | JSON の入れ子（`contents` → `parts` → `text`） |
| PowerShell で `-H` が認識されない | Mac 用の `curl` を貼っている。Windows 欄の例を使う |
| PowerShell で JSON エラー | Windows 欄の `Invoke-RestMethod` 例を使う |
