namespace ProjectBeacon.Application.Common;

using SharpToken;

public static class Tokenizer
{
    public const string HeuristicId = "js_length_div_4";
    public const string RealId = "cl100k_base";

    public static string TokenizerId => RealId;

    private static readonly GptEncoding Encoding = GptEncoding.GetEncoding(RealId);

    public static int EstimateTokens(string? text) => CountTokens(text);

    public static int CountTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        return Encoding.CountTokens(text);
    }

    public static int EstimateHeuristic(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var ascii = 0;
        var other = 0;
        foreach (var c in text)
        {
            if (c < 128)
                ascii++;
            else
                other++;
        }

        return (ascii / 4) + other;
    }
}
