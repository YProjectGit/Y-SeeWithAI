// HttpDisplay.cs
// Request / Response ペインに出す文字列を組み立てる道具。通信はしない。
//
// 教材として「何を送って何が返ったか」を画面で追えるようにするための整形だけを行う。
// APIキーは Mask して出す（キー自体は画面にもログにも出さない）。
//
// 使っているデモ: 2A / 2B / 2C / 3A

using System.Text;

/// <summary>
/// HTTP のリクエスト / レスポンスを画面表示用の文字列にする道具。状態を持たない。
/// </summary>
public static class HttpDisplay
{
    // POST の URL・ヘッダ（キーはマスク）・整形した本文
    // base64MaxChars が 0 以下なら "data":"..." の省略をしない
    public static string FormatRequest(string url, string requestJson, string apiKey, int base64MaxChars)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("POST " + url);
        sb.AppendLine("Content-Type: application/json; charset=utf-8");
        sb.AppendLine("x-goog-api-key: " + GeminiKey.Mask(apiKey));
        sb.AppendLine();
        sb.Append(GeminiJson.PrettyPrint(GeminiJson.TruncateBase64(requestJson, base64MaxChars)));
        return sb.ToString();
    }

    // HTTP ステータスコード + 整形した生レスポンス
    public static string FormatResponse(long statusCode, string responseBody)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("HTTP " + statusCode);
        sb.AppendLine();
        sb.Append(string.IsNullOrEmpty(responseBody) ? "(empty body)" : GeminiJson.PrettyPrint(responseBody));
        return sb.ToString();
    }
}
