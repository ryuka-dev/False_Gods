using FalseGods.Application.Presentation;
using Xunit;

namespace FalseGods.ApplicationTests
{
    /// <summary>
    /// The boss's name is the one piece of this mod every player reads, in whatever language they play in - so the
    /// table behind it is asserted by code point rather than by character.
    /// </summary>
    /// <remarks>
    /// <b>The escapes are the point.</b> <c>FalseGodTitle.cs</c> is the first file in this repository with
    /// non-ASCII source in it. Writing the expected words as <c>\uXXXX</c> here keeps this file plain ASCII, so a
    /// build that decoded that one with the wrong code page fails these tests instead of shipping mojibake to a
    /// player. Each escape carries a romanisation beside it.
    /// </remarks>
    public sealed class FalseGodTitleTests
    {
        /// <summary>The interpunct as most scripts set it: spaced.</summary>
        private const string SpacedInterpunct = " \u00B7 ";

        /// <summary>The languages SULFUR v0.18.5 ships, by the code its localisation layer reports.</summary>
        private static readonly string[] ShippedLanguageCodes =
        {
            "en", "sv", "fr", "it", "de", "es", "pt", "ru", "pl", "ja", "ko", "zh-CN", "tr", "ar",
        };

        [Theory]
        [InlineData("en", "False God", SpacedInterpunct)]
        [InlineData("sv", "Falsk gud", SpacedInterpunct)]
        [InlineData("fr", "Faux Dieu", SpacedInterpunct)]
        [InlineData("it", "Falso Dio", SpacedInterpunct)]
        [InlineData("de", "Falscher Gott", SpacedInterpunct)]
        [InlineData("es", "Falso Dios", SpacedInterpunct)]
        [InlineData("pt", "Falso Deus", SpacedInterpunct)]
        [InlineData("ru", "\u041B\u0436\u0435\u0431\u043E\u0433", SpacedInterpunct)]          // Lzhebog
        [InlineData("pl", "Fa\u0142szywy B\u00F3g", SpacedInterpunct)]                        // Falszywy Bog
        [InlineData("ja", "\u507D\u795E", "\u30FB")]                                          // gishin, middle dot
        [InlineData("ko", "\uAC70\uC9D3 \uC2E0", SpacedInterpunct)]                           // geojit sin
        [InlineData("zh-CN", "\u4F2A\u795E", "\u00B7")]                                       // weishen, interpunct
        [InlineData("tr", "Sahte Tanr\u0131", SpacedInterpunct)]                              // Sahte Tanri
        [InlineData("ar", "\u0625\u0644\u0647 \u0632\u0627\u0626\u0641", SpacedInterpunct)]   // ilah za'if
        public void Each_shipped_language_says_it_its_own_way(string code, string word, string joiner)
        {
            var title = FalseGodTitle.For(code);

            Assert.Equal(word, title.Word);
            Assert.Equal(joiner, title.Joiner);
        }

        [Fact]
        public void Every_language_the_game_ships_has_a_translation_of_its_own()
        {
            var english = FalseGodTitle.For("en");

            foreach (var code in ShippedLanguageCodes)
            {
                var title = FalseGodTitle.For(code);

                Assert.False(string.IsNullOrWhiteSpace(title.Word));
                Assert.False(string.IsNullOrEmpty(title.Joiner));
                if (code != "en")
                {
                    // Not the fallback wearing another language's code: a missing entry fails here, not in a fight.
                    Assert.NotEqual(english.Word, title.Word);
                }
            }
        }

        [Fact]
        public void A_language_we_have_no_words_for_is_shown_in_english()
        {
            Assert.Equal("False God", FalseGodTitle.For("cy").Word);
            Assert.Equal("False God", FalseGodTitle.For(null).Word);
            Assert.Equal("False God", FalseGodTitle.For(string.Empty).Word);
        }

        [Theory]
        [InlineData("zh")]      // the language with no region at all
        [InlineData("zh-TW")]   // a region this table does not carry
        [InlineData("zh-Hans")] // a script subtag rather than a region
        public void A_region_we_do_not_carry_still_gets_its_language(string code)
        {
            Assert.Equal(FalseGodTitle.For("zh-CN").Word, FalseGodTitle.For(code).Word);
        }

        [Fact]
        public void A_code_is_matched_however_it_is_cased()
        {
            Assert.Equal(FalseGodTitle.For("zh-CN").Word, FalseGodTitle.For("ZH-cn").Word);
            Assert.Equal(FalseGodTitle.For("ja").Word, FalseGodTitle.For("JA").Word);
        }

        [Fact]
        public void The_name_reads_title_then_creature()
        {
            // "weishen" + interpunct + "gebulin biaoge", the whole thing unspaced, as Chinese sets it.
            Assert.Equal(
                "\u4F2A\u795E\u00B7\u54E5\u5E03\u6797\u8868\u54E5",
                FalseGodTitle.For("zh-CN").Compose("\u54E5\u5E03\u6797\u8868\u54E5"));

            Assert.Equal("False God \u00B7 Cousin", FalseGodTitle.For("en").Compose("Cousin"));
        }

        /// <summary>
        /// The game hands out right-to-left text already reordered for display, so a title placed in front of the
        /// creature's name would be the part read last. The halves swap instead.
        /// </summary>
        [Fact]
        public void A_right_to_left_language_puts_the_title_where_it_is_read_first()
        {
            var arabic = FalseGodTitle.For("ar");

            var composed = arabic.Compose("NAME", rightToLeft: true);

            Assert.Equal("NAME" + arabic.Joiner + arabic.Word, composed);
            Assert.NotEqual(arabic.Compose("NAME"), composed);
        }

        [Fact]
        public void A_creature_with_no_name_to_borrow_is_announced_by_the_title_alone()
        {
            var english = FalseGodTitle.For("en");

            Assert.Equal("False God", english.Compose(null));
            Assert.Equal("False God", english.Compose(string.Empty));
            Assert.Equal("False God", english.Compose(null, rightToLeft: true));
        }
    }
}
