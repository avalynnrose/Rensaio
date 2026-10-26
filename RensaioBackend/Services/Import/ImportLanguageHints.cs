namespace RensaioBackend.Services.Import
{
    /// <summary>
    /// Folders that Rensaio did not write itself carry no provider or language, so the importer assumes "en".
    /// Titles written in CJK scripts will never be found by English-only providers, so this suggests the
    /// extra languages to search as well.
    /// </summary>
    public static class ImportLanguageHints
    {
        /// <summary>
        /// Returns <paramref name="languages"/> plus any languages implied by the script of <paramref name="title"/>.
        /// Kana implies "ja", Hangul "ko"; Han characters alone are ambiguous so "ja" and "zh" are both added.
        /// The input is never reduced, and the result has no duplicates (case-insensitive).
        /// </summary>
        public static List<string> WithScriptLanguages(IEnumerable<string> languages, string? title)
        {
            List<string> result = new List<string>();
            foreach (string l in languages)
            {
                if (!result.Contains(l, StringComparer.InvariantCultureIgnoreCase))
                    result.Add(l);
            }
            if (string.IsNullOrWhiteSpace(title))
                return result;

            bool kana = false, hangul = false, han = false;
            foreach (char c in title)
            {
                if ((c >= '぀' && c <= 'ヿ') || (c >= 'ㇰ' && c <= 'ㇿ') || (c >= 'ｦ' && c <= 'ﾟ'))
                    kana = true;
                else if ((c >= '가' && c <= '힯') || (c >= 'ᄀ' && c <= 'ᇿ') || (c >= '㄰' && c <= '㆏'))
                    hangul = true;
                else if ((c >= '一' && c <= '鿿') || (c >= '㐀' && c <= '䶿'))
                    han = true;
            }
            List<string> extra = new List<string>();
            if (kana)
                extra.Add("ja");
            if (hangul)
                extra.Add("ko");
            if (han && !kana && !hangul)
            {
                extra.Add("ja");
                extra.Add("zh");
            }
            foreach (string l in extra)
            {
                if (!result.Contains(l, StringComparer.InvariantCultureIgnoreCase))
                    result.Add(l);
            }
            return result;
        }
    }
}
