using System.Globalization;
using System.Text;

namespace WindowScatter;

internal static class OverviewTitleSearch
{
    internal static string Fold(string text)
    {
        if (Ascii.IsValid(text)) return text;
        for (int i = 0; i < text.Length; i++)
        {
            if (!char.IsSurrogate(text[i])) continue;
            if (char.IsSurrogatePair(text, i))
            {
                i++;
                continue;
            }
            // Replace unpaired UTF-16 code units before normalization rejects the whole title.
            text = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text));
            break;
        }

        var result = new StringBuilder(text.Length);
        bool afterVowel = false;
        foreach (var character in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                if (afterVowel) continue;
            }
            else
            {
                afterVowel = char.ToLowerInvariant(character) is 'a' or 'e' or 'i' or 'o' or 'u';
            }
            result.Append(character);
        }
        // Recompose consonants so an ordinal search for n cannot match decomposed ñ.
        return result.ToString().Normalize(NormalizationForm.FormC);
    }

    internal static bool Matches(string foldedTitle, string foldedQuery) =>
        foldedTitle.Contains(foldedQuery, StringComparison.OrdinalIgnoreCase);
}
