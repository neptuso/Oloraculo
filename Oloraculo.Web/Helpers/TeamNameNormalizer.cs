using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Oloraculo.Web.Helpers
{
    public class TeamNameNormalizer
    {
        private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["usa"] = "United States",
            ["u.s.a"] = "United States",
            ["usmnt"] = "United States",
            ["united states of america"] = "United States",
            ["bosnia"] = "Bosnia and Herzegovina",
            ["bosnia herzegovina"] = "Bosnia and Herzegovina",
            ["korea republic"] = "South Korea",
            ["republic of korea"] = "South Korea",
            ["south korea"] = "South Korea",
            ["turkiye"] = "Turkey",
            ["türkiye"] = "Turkey",
            ["czech republic"] = "Czechia",
            ["cote d'ivoire"] = "Ivory Coast",
            ["côte d’ivoire"] = "Ivory Coast",
            ["dr congo"] = "Congo DR",
            ["congo dr"] = "Congo DR",
            ["iran"] = "Iran",
            ["ir iran"] = "Iran"
        };
        public static string CanonicalName(string Name)
        {
            var cleaned = Regex.Replace(Name.Trim(), "\\s+", " ");
            return Aliases.TryGetValue(RemoveDiacritics(cleaned).ToLowerInvariant(), out var alias)
                ? alias
                : cleaned;
        }
        private static string RemoveDiacritics(string text) 
        {
            string Normalized = text.Normalize(System.Text.NormalizationForm.FormD);
            var Builder = new StringBuilder(Normalized.Length);
            foreach(var ch in Normalized)
            {
                if(CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                {
                    Builder.Append(ch);
                }
            }
            return Builder.ToString().Normalize(NormalizationForm.FormC);
        }
        public static string ToId(string Name)
        {
            var Canonical = CanonicalName(Name);
            var Ascii = RemoveDiacritics(Canonical).ToLowerInvariant();
            Ascii = Regex.Replace(Ascii, "[^a-z0-9]+", "-").Trim('-');
            return Ascii;
        }
    }
}
