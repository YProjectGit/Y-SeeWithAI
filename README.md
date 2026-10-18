# Y-SeeWithAI

学生が Gemini の API を触って、映像・画像のインタラクションをつくるワークショップ教材です。

テキストと音声の先行教材 → [Y-TalkWithAI](https://github.com/YProjectGit/Y-TalkWithAI)

学習順と各デモの位置づけ → [Assets/Docs/demo-series-overview.md](Assets/Docs/demo-series-overview.md)

---

## 必要なもの

- **Unity 6.3**（このプロジェクトは `6000.3.6f1`）
- **Google アカウント**（APIキー用。TalkWithAI で取得済みなら、同じキーを使えます）
- **カメラ**（1.VisionToSpeech）
- **スピーカーまたはヘッドホン**（1 / 2 の音声再生）

---

## 教材のダウンロード

[GitHub Releases](https://github.com/YProjectGit/Y-SeeWithAI/releases/latest) の Assets から **Y-SeeWithAI.zip** をダウンロードし、展開してください。

## 最初にやること

1. Unity Hub でこのフォルダを開き、バージョン `6000.3.6f1` で開いてください。
2. Google AI Studio から Gemini の API にアクセスするための APIキーを取得し、`Assets/Common/APIKey.txt` に保管してください。  
   手順 → [Assets/Docs/gemini-ai-studio-setup.md](Assets/Docs/gemini-ai-studio-setup.md)
3. Project ウィンドウで `Assets/1.VisionToSpeech/VisionToSpeech.unity` を開き、Play してください。

無料枠で 429 が出たら → [Assets/Docs/gemini-api-pricing.md](Assets/Docs/gemini-api-pricing.md)

---

## デモ一覧

上から学習していく順です。詳細は各フォルダの README を見てください。

| デモ | 学ぶこと |
|------|----------|
| [1.VisionToSpeech](Assets/1.VisionToSpeech/) | カメラ映像について声で話す |
| [2.ScreenToSpeech](Assets/2.ScreenToSpeech/) | アプリ自身が描く画面を声で解釈する |
