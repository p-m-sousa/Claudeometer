using System;
using System.Globalization;

namespace ClaudeUsage.Core.Internal
{
    internal static class DateKey
    {
        internal static bool IsValid(string value)
        {
            if (value == null || value.Length != 10 || value[4] != '-' || value[7] != '-')
            {
                return false;
            }

            int year;
            int month;
            int day;
            if (!TryParsePart(value, 0, 4, out year)
                || !TryParsePart(value, 5, 2, out month)
                || !TryParsePart(value, 8, 2, out day))
            {
                return false;
            }

            if (year < 1 || year > 9999 || month < 1 || month > 12 || day < 1)
            {
                return false;
            }

            var daysInMonth = DaysInMonth(year, month);
            return day <= daysInMonth;
        }

        internal static string ValidateBound(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (!IsValid(value))
            {
                throw new ArgumentException("Date filters must use a valid YYYY-MM-DD value.", parameterName);
            }

            return value;
        }

        internal static bool IsWithinInclusiveRange(string date, string fromDate, string toDate)
        {
            return (fromDate == null || string.CompareOrdinal(date, fromDate) >= 0)
                && (toDate == null || string.CompareOrdinal(date, toDate) <= 0);
        }

        private static bool TryParsePart(string value, int start, int length, out int result)
        {
            return int.TryParse(
                value.Substring(start, length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out result);
        }

        private static int DaysInMonth(int year, int month)
        {
            switch (month)
            {
                case 2:
                    return IsLeapYear(year) ? 29 : 28;
                case 4:
                case 6:
                case 9:
                case 11:
                    return 30;
                default:
                    return 31;
            }
        }

        private static bool IsLeapYear(int year)
        {
            return year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);
        }
    }
}
