# Gemini API 支払い・価格ガイド

音声・画像インタラクション・ワークショップ用 / 無料枠の回数に達してから有料へ移る  
（最終確認: 2026年8月。手順・金額は [公式の課金](https://ai.google.dev/gemini-api/docs/billing) と [料金表](https://ai.google.dev/gemini-api/docs/pricing) を正とします）

---

## やること

1. **まず無料のまま使う**（キー取得は [gemini-ai-studio-setup.md](gemini-ai-studio-setup.md)）
2. Unity で **HTTP 429** が出たら、無料枠の回数を確認する
3. 授業を続けたいときだけ、**同じプロジェクトを有料にする**
4. **上限を先に付ける**（使いすぎ防止）

> 使うのは **Google AI Studio の Gemini API 課金**です。  
> 左メニューの **Upgrade**（Google AI の月額プラン）とは別物です。押さないでください。

必要なもの: 無料のうちは Google アカウントだけ。有料に移るときはクレジットカード（または AI Studio が受け付ける支払い方法）。

---

## 無料のあいだ

APIキーを取った直後は **Free Tier** です。トークン代はかかりません。代わりに、モデルごとに **1分あたり・1日あたりの回数** が決まっています。

自分の上限は次で見てください。

1. [Google AI Studio](https://aistudio.google.com) を開く
2. **Usage & Billing**（使用状況と請求）→ **Rate limits**（レート制限）

このワークショップでよく使う枠の目安です（2026年8月時点）。実際の上限はアカウントによって違うので、AI Studio の Rate limits 画面の数字を正としてください。表の STT は Speech-to-Text（音声→テキスト）、TTS は Text-to-Speech（テキスト→音声）の略です。

| モデル | よくある無料の1日回数 | この教材での使いどころ |
|---|---|---|
| `gemini-3.1-flash-lite` | 約 500回 | 1A〜3A のテキスト / STT / チャット（この教材の既定） |
| `gemini-3.6-flash` など通常の Flash | 約 20回 | 使わない（無料枠が小さい） |
| Live / TTS 向けモデル | モデルごとに別枠 | 3A の声、3B / 3C / 4 / 5 の Live |
| `gemini-3.1-flash-image` など画像モデル | モデルごとに別枠（Lite テキストより少ないことが多い） | 6 の画像生成（7 も同じ系統） |

回数は **プロジェクト × モデル** です。Flash を使い切っても、Lite の枠は残っていることがあります。

---

## 上限に達したとき

Unity の右ペイン（Response）に次が出たら、無料枠です。

- `HTTP 429`
- `RESOURCE_EXHAUSTED`
- `generate_content_free_tier_requests`
- `limit: 20` や `GenerateRequestsPerDayPerProjectPerModel-FreeTier`

「数秒後に再試行」と書いてあっても、**日次の回数を使い切っていると待ちでは直りません**（リセットは日付が変わってから。どの時刻で変わるかはアカウント側のタイムゾーンに従います）。

続けるなら、次の「有料に移る」へ進んでください。キーを作り直す必要はありません。

---

## 有料に移る

公式手順の要約です。画面の文言が英語のことがあります。

### 1. 課金を始める

1. [API keys](https://aistudio.google.com/app/apikey) または **Projects** を開く
2. 今使っているプロジェクトの **Billing Tier** 列で **Set up billing** を押す
3. 初めてなら国・利用規約・連絡先・支払い方法を入れる
4. すでに Google の請求アカウントがあるなら、それを選ぶか **Add new billing account** する

### 2. クレジットを入れる（Prepay）

2026年3月以降、新規は **先払い（Prepay）** になることが多いです。画面の案内に従ってください。

- 最低チャージは **$10**（他通貨の同額）
- 残高から、使った分だけリアルタイムに引かれます
- 残高が **$0** になると、その請求アカウントに繋がるキーはすべて止まります（自動で無料に戻りません）
- 余ったクレジットは **購入から12か月で失効**し、原則返金されません

**自動チャージ（auto-reload）はオフのまま**にしてください。授業では $10 を使い切ったら止める方が安全です。

画面が Postpay（後払い）を出したら、選んでよいですが、その場合は次の「月次上限」を必ず付けてください。

### 3. 月次の上限を付ける

1. AI Studio の **Spend**（支出）を開く
2. **Monthly spend cap** → **Edit spend cap**
3. 授業用なら **$5〜10** を入れる

上限に達すると、そのプロジェクトの API は止まります。ただし反映まで **約10分** かかることがあり、そのあいだに上限を少し超える場合があります。Live を繋いだままにしないでください。

### 4. Unity 側

`Assets/Common/APIKey.txt` のキーはそのまま使えます。差し替え不要です。Play して、もう一度送ってください。

有料になると、同じ 429 は出にくくなります（回数上限が上がるため）。トークン代はかかるようになります。

---

## 有料のあとのコスト目安

従量課金です。月額の定額ではありません。単価は 100万トークンあたり（2026年8月、Standard。`gemini-3.6-flash` は 2026年末までの価格）。

| 用途 | モデル | 入力 | 出力 |
|---|---|---|---|
| テキスト / JSON | `gemini-3.1-flash-lite` | $0.25 | $1.50 |
| テキスト / JSON | `gemini-3.6-flash` | $0.75 | $3.75 |
| TTS（3A） | `gemini-3.1-flash-tts-preview` | $1.00（テキスト） | $20.00（音声） |
| Live（3B / 3C / 4 / 5） | `gemini-3.1-flash-live-preview` | 音声 約 $0.005/分 | 音声 約 $0.018/分 |

1ドル ≈ 150円で見た、**学生1人・半日**の目安です。

| 使い方 | だいたい |
|---|---|
| 1A / 1B の短い送信（Lite） | 1回 0.1円未満。半日でも数円〜20円 |
| 2A / 3A（文字起こし + 返信 + TTS） | 20〜80円 |
| Live を 10〜15分 | 追加で 50〜150円 |
| Live を繋ぎっぱなし 1時間 | 追加で 約200円 |
| 授業全体（Live を触る） | **100〜200円** に収まりやすい |

`$10` 先払いなら、普通の授業では残高が余ります。高いのは **Live を切らずに放置**したときです。Play を止めるとセッションが切れます。

Google Cloud の新規 **$300 クレジットは Gemini API に使えません**（2026年3月以降）。AI Studio で入れた Prepay 残高だけが API に使えます。

---

## 確認する場所

| 見たいもの | 場所 |
|---|---|
| 無料の残り回数 | AI Studio → Usage & Billing → Rate limits |
| 課金の状態 | [Billing](https://ai.google.dev/gemini-api/docs/billing) の案内どおり、AI Studio の Billing / Projects |
| 今日の使用量 | Dashboard → Usage |
| 公式の単価 | [Pricing](https://ai.google.dev/gemini-api/docs/pricing) |

---

## つまずいたとき

| 症状 | 確認 |
|---|---|
| まだ 429 | Rate limits で **どのモデル** が赤いか。Lite と Flash は別枠。有料の反映まで数分かかることがある |
| 有料なのに止まった | Prepay 残高が $0。Spend cap 到達。カード決済の失敗 |
| Upgrade を押してしまった | Google AI の月額プランです。API の回数とは別。キャンセルして **Set up billing** に戻る |
| いくら使ったか分からない | Usage と Billing。グラフは最大24時間遅れることがある |
| 無料に戻したい | そのプロジェクトの課金を切る（[公式 FAQ](https://ai.google.dev/gemini-api/docs/billing)）。キーは残りますが、回数は無料枠に戻る |
