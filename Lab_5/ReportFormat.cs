// Единый формат чисел в консольном отчёте: пробелы между разрядами, запятая в дробной части.
using System.Globalization;

namespace Lab5.ConcurrentCollections;

internal static class ReportFormat
{
    public const string OperationsPerSecond = "операций/сек";

    private static readonly CultureInfo Culture = CreateCulture();

    private static CultureInfo CreateCulture()
    {
        CultureInfo culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NumberGroupSeparator = " ";
        culture.NumberFormat.NumberDecimalSeparator = ",";
        return culture;
    }

    public static string Number(double value, int decimals = 2) =>
        value.ToString($"N{decimals}", Culture);

    public static string Integer(int value) => value.ToString("N0", Culture);

    public static string Integer(long value) => value.ToString("N0", Culture);

    public static string Integrity(bool isValid) => isValid ? "Да" : "Нет";
}
