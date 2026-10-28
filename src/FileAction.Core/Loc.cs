using System.Globalization;

namespace FileAction.Core;

public enum Language
{
    English,
    Czech,
}

/// <summary>
/// Tiny two-language localization: every user-facing string is written as <c>T("česky", "English")</c>
/// at its call site, so the context stays next to the text.
/// </summary>
public static class Loc
{
    public static Language Current { get; set; } = Detect();

    /// <summary>Czech for Czech and Slovak Windows, English everywhere else.</summary>
    public static Language Detect() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is "cs" or "sk" ? Language.Czech : Language.English;

    public static string T(string czech, string english) => Current == Language.Czech ? czech : english;

    /// <summary>"1 item", "3 items" / "1 položka", "3 položky", "5 položek".</summary>
    public static string Items(int n) => Current == Language.Czech
        ? n switch
        {
            1 => "1 položka",
            >= 2 and <= 4 => $"{n} položky",
            _ => $"{n} položek",
        }
        : n == 1 ? "1 item" : $"{n} items";
}
