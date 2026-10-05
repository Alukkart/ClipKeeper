using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DeviceGuard
{
    // Interface language. English is the base language, Russian is the second one.
    // Strings live next to the code that shows them: L.T("Save", "Сохранить").
    // In XAML an attribute value "Save¦Сохранить" is resolved when the window is loaded (see Wpf.Load).
    // Logs, test reports and code comments are English only.
    static class L
    {
        public const string Auto = "auto", En = "en", Ru = "ru";

        public static bool IsRu { get; private set; }
        public static string Code { get { return IsRu ? Ru : En; } }
        public static CultureInfo Culture { get { return CultureInfo.GetCultureInfo(IsRu ? "ru-RU" : "en-US"); } }

        // "auto" follows the Windows display language
        public static void Init(string setting)
        {
            IsRu = setting == Ru || (setting != En && SystemIsRu);
        }

        public static bool SystemIsRu
        {
            get { return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru"; }
        }

        public static string T(string en, string ru) { return IsRu ? ru : en; }

        // "3 clips" / "3 клипа"
        public static string N(int n, string enOne, string enMany, string ruOne, string ruFew, string ruMany)
        {
            return n + " " + W(n, enOne, enMany, ruOne, ruFew, ruMany);
        }

        // the word alone: "clips" / "клипа"
        public static string W(int n, string enOne, string enMany, string ruOne, string ruFew, string ruMany)
        {
            return IsRu ? Plural(n, ruOne, ruFew, ruMany) : (n == 1 ? enOne : enMany);
        }

        // Russian plural forms: 1 клип, 2 клипа, 5 клипов, 11 клипов, 21 клип
        public static string Plural(int n, string one, string few, string many)
        {
            n = Math.Abs(n) % 100;
            if (n >= 11 && n <= 14) return many;
            n %= 10;
            return n == 1 ? one : n >= 2 && n <= 4 ? few : many;
        }

        static readonly Regex XamlAttr = new Regex("=\"([^\"¦]*)¦([^\"]*)\"", RegexOptions.Compiled);
        static readonly Regex XamlText = new Regex(">([^<¦]*)¦([^<]*)<", RegexOptions.Compiled);

        public static string Xaml(string text)
        {
            text = XamlAttr.Replace(text, m => "=\"" + m.Groups[IsRu ? 2 : 1].Value + "\"");
            return XamlText.Replace(text, m => ">" + m.Groups[IsRu ? 2 : 1].Value + "<");
        }
    }
}
