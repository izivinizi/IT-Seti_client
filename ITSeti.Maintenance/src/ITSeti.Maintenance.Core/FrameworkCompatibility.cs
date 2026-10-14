namespace ITSeti.Maintenance.Core
{
    public static class Numeric
    {
        public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
#if NETFRAMEWORK
    internal static class StringCompatibility
    {
        public static bool Contains(this string text, string value, StringComparison comparison) => text.IndexOf(value, comparison) >= 0;
    }
#endif
}
#if NETFRAMEWORK
namespace System.Runtime.CompilerServices { internal static class IsExternalInit { } }
#endif
