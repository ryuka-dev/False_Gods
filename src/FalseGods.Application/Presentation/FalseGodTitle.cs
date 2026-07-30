using System;
using System.Collections.Generic;

namespace FalseGods.Application.Presentation
{
    /// <summary>
    /// What this mod calls one of its own bosses in the player's language: the word for a false god, and the mark
    /// that separates it from the name of the creature it is announced with.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a prefix at all.</b> Our boss borrows a vanilla creature's definition, so the game's own boss
    /// bar announces it by that creature's localised name (Docs/BossEncounterRunbook.md §2.9). Prefixing keeps that
    /// translation — free, and correct in every language the game ships — while saying plainly that this is not the
    /// creature the player has met before: the thing in the arena drank what leaked out of a shrine and came to
    /// believe it was a god.</para>
    /// <para><b>This table is the mod's own translation, and it is not the game's.</b> The game's text lives in an
    /// I2 Localization source loaded from its own content; a term of ours is not in it, and adding one would mean
    /// writing into a shared, Addressables-owned asset that the game reloads and swaps sources on. So the mod owns
    /// its words here and only <i>asks</i> the game which language is current — which is why this file is a plain
    /// table with no dependency on any localisation library, and why it can be unit-tested without a game.</para>
    /// <para><b>Keyed by language code, not by language name.</b> Codes are what the game's own settings carry
    /// through to the localisation layer, they are short, and they do not move when a display name is reworded. The
    /// fourteen entries below are exactly the languages SULFUR v0.18.5 ships (measured off its I2 source); anything
    /// else falls back, first to the language without its region and then to English, so an unknown language shows
    /// a readable name rather than nothing.</para>
    /// <para><b>Right-to-left is an ordering question, and it is asked here.</b> See <see cref="Compose"/>.</para>
    /// </remarks>
    public sealed class FalseGodTitle
    {
        /// <summary>The interpunct, as Latin, Cyrillic and Korean set it: spaced, because those scripts space words.</summary>
        private const string SpacedInterpunct = " · ";

        /// <summary>The interpunct as Chinese sets it: unspaced, because Chinese does not space words.</summary>
        private const string ChineseInterpunct = "·";

        /// <summary>Japanese uses its own katakana middle dot for the same job.</summary>
        private const string JapaneseInterpunct = "・";

        /// <summary>English, and what every unknown language is shown as.</summary>
        private static readonly FalseGodTitle Fallback = new FalseGodTitle("False God", SpacedInterpunct);

        /// <summary>
        /// The fourteen languages SULFUR ships, by the code its localisation layer reports.
        /// </summary>
        /// <remarks>
        /// Each entry is a translation of the concept, not of the English words: Russian and Japanese both have a
        /// single word for a false god and use it, and Korean has no such compound so it says it in two.
        /// </remarks>
        private static readonly Dictionary<string, FalseGodTitle> ByLanguageCode =
            new Dictionary<string, FalseGodTitle>(StringComparer.OrdinalIgnoreCase)
            {
                ["en"] = Fallback,
                ["sv"] = new FalseGodTitle("Falsk gud", SpacedInterpunct),
                ["fr"] = new FalseGodTitle("Faux Dieu", SpacedInterpunct),
                ["it"] = new FalseGodTitle("Falso Dio", SpacedInterpunct),
                ["de"] = new FalseGodTitle("Falscher Gott", SpacedInterpunct),
                ["es"] = new FalseGodTitle("Falso Dios", SpacedInterpunct),
                ["pt"] = new FalseGodTitle("Falso Deus", SpacedInterpunct),
                ["ru"] = new FalseGodTitle("Лжебог", SpacedInterpunct),
                ["pl"] = new FalseGodTitle("Fałszywy Bóg", SpacedInterpunct),
                ["ja"] = new FalseGodTitle("偽神", JapaneseInterpunct),
                ["ko"] = new FalseGodTitle("거짓 신", SpacedInterpunct),
                ["zh-CN"] = new FalseGodTitle("伪神", ChineseInterpunct),
                ["tr"] = new FalseGodTitle("Sahte Tanrı", SpacedInterpunct),
                ["ar"] = new FalseGodTitle("إله زائف", SpacedInterpunct),
            };

        private FalseGodTitle(string word, string joiner)
        {
            Word = word;
            Joiner = joiner;
        }

        /// <summary>The word this language uses for a false god.</summary>
        public string Word { get; }

        /// <summary>The mark set between the word and the creature's name, spaced as this language spaces it.</summary>
        public string Joiner { get; }

        /// <summary>
        /// The title for a language code (<c>en</c>, <c>zh-CN</c>, …), falling back to the language without its
        /// region and then to English. Never null.
        /// </summary>
        public static FalseGodTitle For(string? languageCode)
        {
            if (string.IsNullOrEmpty(languageCode))
            {
                return Fallback;
            }

            if (ByLanguageCode.TryGetValue(languageCode!, out var exact))
            {
                return exact;
            }

            // Match on the language alone when the region does not line up. "zh-TW" is not a language this table
            // has, but it is far closer to the entry filed under "zh-CN" than to English, and a code carrying a
            // region we do not know is the likeliest shape of a language we have not been asked for yet.
            var wanted = LanguageOf(languageCode!);
            if (ByLanguageCode.TryGetValue(wanted, out var broader))
            {
                return broader;
            }

            foreach (var entry in ByLanguageCode)
            {
                if (string.Equals(LanguageOf(entry.Key), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.Value;
                }
            }

            return Fallback;
        }

        /// <summary>A language code without whatever follows its first hyphen: <c>zh-CN</c> and <c>zh-Hans</c> are
        /// both <c>zh</c>.</summary>
        private static string LanguageOf(string languageCode)
        {
            var hyphen = languageCode.IndexOf('-');
            return hyphen > 0 ? languageCode.Substring(0, hyphen) : languageCode;
        }

        /// <summary>
        /// The full name to show: this title, the joiner, and the creature's own localised name.
        /// </summary>
        /// <remarks>
        /// <para><b>Why the order is a parameter.</b> The game hands out right-to-left text already reordered for
        /// display — its localisation layer runs an RTL fixer over a translation before returning it — so an Arabic
        /// creature name arrives in visual order and is drawn left to right like any other string. Putting our
        /// title in front of it would therefore put it at the <i>end</i> of the line as an Arabic reader sees it.
        /// For a right-to-left language the two halves are emitted in the opposite order, which lands the title
        /// where it is read first.</para>
        /// <para><b>Only the ordering is decided here.</b> Shaping our own word for such a language is the
        /// localisation layer's job and happens at the call site, on the way in.</para>
        /// <para>A creature with no name to borrow is announced by the title alone rather than by a stray mark.</para>
        /// </remarks>
        public static string Compose(string word, string joiner, string? creatureName, bool rightToLeft)
        {
            if (string.IsNullOrEmpty(creatureName))
            {
                return word;
            }

            return rightToLeft ? creatureName + joiner + word : word + joiner + creatureName;
        }

        /// <summary>
        /// <see cref="Compose(string, string, string?, bool)"/> with this title's own word and joiner.
        /// </summary>
        public string Compose(string? creatureName, bool rightToLeft = false) =>
            Compose(Word, Joiner, creatureName, rightToLeft);
    }
}
