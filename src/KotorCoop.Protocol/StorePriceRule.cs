namespace KotorCoop.Protocol;

/// <summary>Applies the negotiated store-price percent after KOTOR's own buy/sell
/// calculation. Retail has already performed its integer conversion when this rule receives
/// the value, so 100% is an exact identity and every other setting uses a wide integer
/// multiply followed by floor division. A positive priced item always costs or pays at least
/// one credit; a retail zero (including plot items) remains zero.</summary>
public static class StorePriceRule
{
    public static int ScaleRetailPrice(int retailPrice, int percent)
    {
        if (retailPrice <= 0 || percent == PlayerCountScalingRule.Vanilla)
        {
            return retailPrice;
        }

        percent = PlayerCountScalingRule.ClampPercent(percent);
        long scaled = (long)retailPrice * percent / 100;
        if (scaled < 1)
        {
            return 1;
        }

        return scaled > int.MaxValue ? int.MaxValue : (int)scaled;
    }
}
