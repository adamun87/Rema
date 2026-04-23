using System;
using System.Globalization;
using Avalonia.Markup.Xaml;

namespace Rema.Localization;

public static partial class Loc
{
    public static CultureInfo Culture { get; private set; } = CultureInfo.CurrentUICulture;
    public static bool IsRightToLeft => Culture.TextInfo.IsRightToLeft;

    public static void Load(string language)
    {
        SelectLanguage(language);
        try
        {
            Culture = new CultureInfo(language);
            CultureInfo.CurrentUICulture = Culture;
            CultureInfo.CurrentCulture = Culture;
        }
        catch
        {
            Culture = CultureInfo.InvariantCulture;
        }
    }

    public static string Get(string key) => GetByKey(key);
    public static string Get(string key, params object[] args)
        => string.Format(GetByKey(key), args);
}

public class StrExtension : MarkupExtension
{
    public StrExtension() { }
    public StrExtension(string key) { Key = key; }
    public string Key { get; set; } = "";
    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.Get(Key);
}
