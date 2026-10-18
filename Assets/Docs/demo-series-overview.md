# デモシリーズ全体構成（概要）

映像・画像インタラクション・ワークショップの学習順と、各デモの位置づけです。手順は各フォルダの README を見てください。

テキストと音声は先行教材 [Y-TalkWithAI](https://github.com/YProjectGit/Y-TalkWithAI) で扱います。

---

## 事前準備

1. Unity Hub でこのプロジェクトを開き、バージョン **6000.3.6f1**（Unity 6.3）で開いてください。
2. Google AI Studio から Gemini の API にアクセスするための APIキーを取得し、`Assets/Common/APIKey.txt` に保管してください。  
   手順 → [gemini-ai-studio-setup.md](gemini-ai-studio-setup.md)

---

## デモ一覧

上から学習していく順です。

- **[1.VisionToSpeech](../1.VisionToSpeech/)**<br>
  カメラが捉えたものについて、映像を見せて声で話す
- **[2.ScreenToSpeech](../2.ScreenToSpeech/)**<br>
  アプリ自身が描く画面を見せ、画面の中身を声で解釈する

---

## コードの考え方

- 各デモの本体は、そのフォルダのメインスクリプト1本です。処理は送信 → 待ち → 受信の順です。
- リクエスト JSON の組み立てと、レスポンスからの取り出しは、そのスクリプトに書いてあります。共通化していません。
- 1.VisionToSpeech は画面中央が送信、右が受信です。2.ScreenToSpeech は描画パッドと字幕を中心にした画面です。
- `Assets/Common/Script/` は JSON 整形・APIキー読込・音声変換などのユーティリティです。デモの流れを読むときは見なくてよいです。
- 改変するときはデモフォルダをコピーしてください。`Common` を変えると全デモに波及します。
