using System.Globalization;

namespace DesktopPet.UI;

internal static class CompactCountFormatter
{
    public static string Format(long count)
    {
        if (count <= 99_999) return count.ToString(CultureInfo.InvariantCulture);

        string[] suffixes = ["k", "M", "B", "T", "P", "E"];
        var value = (double)count;
        var suffixIndex = -1;
        do
        {
            value /= 1000;
            suffixIndex++;
        }
        while (value >= 1000 && suffixIndex < suffixes.Length - 1);

        var format = value >= 100 ? "0" : value >= 10 ? "0.#" : "0.##";
        return value.ToString(format, CultureInfo.InvariantCulture) + suffixes[suffixIndex];
    }
}
