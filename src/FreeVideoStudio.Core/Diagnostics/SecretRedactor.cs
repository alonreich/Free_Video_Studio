using System;
using System.Text.RegularExpressions;

namespace FreeVideoStudio.Core.Diagnostics;

/// <summary>
/// Central secret redaction helper shared by Core diagnostics, RuntimeLog, and AI providers.
/// Scrubs API keys (explicit and pattern-matched Google/Anthropic/OpenAI tokens), URL key query
/// parameters, and sensitive authorization headers without mutating unrelated filtergraphs or paths.
/// </summary>
public static partial class SecretRedactor
{
    private static readonly Regex UrlKeyParamPattern = new(@"(?i)(key=)[^&\s""']+", RegexOptions.Compiled);
    private static readonly Regex GoogKeyHeaderPattern = new(@"(?i)(x-goog-api-key\s*[:=]\s*)\S+", RegexOptions.Compiled);
    private static readonly Regex ApiKeyHeaderPattern = new(@"(?i)(x-api-key\s*[:=]\s*)\S+", RegexOptions.Compiled);
    private static readonly Regex AuthHeaderPattern = new(@"(?i)(authorization\s*[:=]\s*(?:(?:bearer|basic|token)\s+)?)\S+", RegexOptions.Compiled);
    private static readonly Regex GoogleKeyShapePattern = new(@"AIza[0-9A-Za-z_\-]{25,}", RegexOptions.Compiled);
    private static readonly Regex AnthropicKeyShapePattern = new(@"sk-ant-[0-9A-Za-z_\-]{16,}", RegexOptions.Compiled);
    private static readonly Regex OpenAiKeyShapePattern = new(@"sk-[0-9A-Za-z_\-]{20,}", RegexOptions.Compiled);

    /// <summary>Redacts API keys, query secrets, and auth headers from arbitrary log/error text.</summary>
    public static string RedactSecrets(string? text, params string?[] explicitSecrets)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        string result = text;
        if (explicitSecrets != null)
        {
            foreach (string? secret in explicitSecrets)
            {
                string trimmed = secret?.Trim() ?? string.Empty;
                if (trimmed.Length > 0)
                    result = result.Replace(trimmed, "[REDACTED]", StringComparison.Ordinal);
            }
        }

        result = UrlKeyParamPattern.Replace(result, "$1REDACTED");
        result = GoogKeyHeaderPattern.Replace(result, "$1REDACTED");
        result = ApiKeyHeaderPattern.Replace(result, "$1REDACTED");
        result = AuthHeaderPattern.Replace(result, "$1REDACTED");
        result = GoogleKeyShapePattern.Replace(result, "[REDACTED]");
        result = AnthropicKeyShapePattern.Replace(result, "[REDACTED]");
        result = OpenAiKeyShapePattern.Replace(result, "[REDACTED]");
        return result;
    }
}
