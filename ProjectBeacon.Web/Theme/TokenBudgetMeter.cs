namespace ProjectBeacon.Web.Theme;

public static class TokenBudgetMeter
{
    public static int Percent(int tokens, int budget)
    {
        if (budget <= 0)
            return tokens > 0 ? 100 : 0;
        return Math.Min(100, (int)Math.Round(tokens * 100.0 / budget));
    }
}
