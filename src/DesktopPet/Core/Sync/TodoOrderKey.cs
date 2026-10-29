using System.Globalization;
using System.Numerics;
using System.Text;

namespace DesktopPet.Core.Sync;

// Decimal fractional keys use ordinal comparison and never end in zero.
public static class TodoOrderKey
{
    public static string Legacy(DateTimeOffset createdAt, Guid id) =>
        (DateTime.MaxValue.Ticks - createdAt.UtcTicks).ToString("D19", CultureInfo.InvariantCulture) +
        Identity(id) + "5";

    private static string Identity(Guid id) =>
        new BigInteger(id.ToByteArray(), isUnsigned: true, isBigEndian: true).ToString("D39", CultureInfo.InvariantCulture);

    public static bool IsValid(string? key) =>
        key is { Length: > 0 and <= 4096 } && key[^1] != '0' && key.All(c => c is >= '0' and <= '9');

    public static string Between(string? lower, string? upper)
    {
        if (lower is not null && upper is not null && string.CompareOrdinal(lower, upper) >= 0)
            throw new ArgumentException("排序边界无效。");
        var result = new StringBuilder();
        for (var i = 0; i < 4000; i++)
        {
            var low = lower is not null && i < lower.Length ? lower[i] - '0' : 0;
            var high = upper is not null && i < upper.Length ? upper[i] - '0' : 10;
            if (high - low > 1)
                return result.Append((char)('0' + (low + high) / 2)).Append(Identity(Guid.NewGuid())).Append('5').ToString();
            result.Append((char)('0' + low));
            if (low < high) upper = null;
        }
        throw new InvalidOperationException("排序位置过于密集，请稍后重试。");
    }
}
