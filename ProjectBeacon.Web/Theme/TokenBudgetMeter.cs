namespace ProjectBeacon.Web.Theme;
/// <summary>
/// Percent of a token budget used, capped at 100. A non-positive budget is 100 when any tokens were used.
/// </summary>
public static class TokenBudgetMeter
{
    public static int Percent(int tokens, int budget)
    {
        if (budget <= 0)
            return tokens > 0 ? 100 : 0;
        return Math.Min(100, (int)Math.Round(tokens * 100.0 / budget));
    }
}
