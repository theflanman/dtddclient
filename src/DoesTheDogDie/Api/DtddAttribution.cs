namespace DoesTheDogDie.Api;

/// <summary>
/// Attribution constants required by the DtDD terms of service on every view that shows DtDD
/// data.
/// </summary>
public static class DtddAttribution
{
    /// <summary>The exact required attribution phrase.</summary>
    public const string Phrase = "Powered by DoesTheDogDie.com";

    /// <summary>The URL the attribution phrase must link to.</summary>
    public const string Url = "https://www.doesthedogdie.com";

    /// <summary>A ready-made HTML attribution link, built from <see cref="Url"/> and <see cref="Phrase"/>.</summary>
    public const string Html = $"<a href=\"{Url}\" target=\"_blank\" rel=\"noopener\">{Phrase}</a>";
}
