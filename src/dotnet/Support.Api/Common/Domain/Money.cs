using System.Globalization;

namespace Support.Api.Common.Domain;

/// <summary>An amount in minor units (cents) and an ISO currency code, exactly as in the shared fixtures.</summary>
public sealed record Money(long AmountMinor, string Currency)
{
    /// <summary>For display only, for example "86.39 USD". Calculations stay in minor units.</summary>
    public string Display() => string.Create(CultureInfo.InvariantCulture, $"{AmountMinor / 100m:0.00} {Currency}");
}
