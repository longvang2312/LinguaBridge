using LinguaBridge.Domain.Enums;
using LinguaBridge.Modules.AiTutor.Prompting;
using LinguaBridge.Modules.AiTutor.Responses;

namespace LinguaBridge.UnitTests.AiTutor;

public class AiTutorPromptBuilderTests
{
    [Fact]
    public void English_prompt_names_the_cefr_level_and_the_vocabulary_rule()
    {
        var prompt = AiTutorPromptBuilder.Build(TargetLanguage.English, LanguageLevel.B1, "FreeTalk", BaseLanguage.Vietnamese);

        Assert.StartsWith("You are an English tutor. The learner is at CEFR B1. Reply only with vocabulary from that level.", prompt);
        Assert.DoesNotContain("HSK", prompt);
    }

    [Theory]
    [InlineData(LanguageLevel.Hsk1, "HSK 1")]
    [InlineData(LanguageLevel.Hsk4, "HSK 4")]
    [InlineData(LanguageLevel.Hsk6, "HSK 6")]
    public void Chinese_prompt_names_the_hsk_number_and_asks_for_pinyin(LanguageLevel level, string expected)
    {
        var prompt = AiTutorPromptBuilder.Build(TargetLanguage.Chinese, level, "FreeTalk", BaseLanguage.English);

        Assert.StartsWith($"你是一位中文老师。学习者的水平是 {expected}。所有汉字请附带拼音。", prompt);
        Assert.DoesNotContain("CEFR", prompt);
    }

    [Theory]
    [InlineData("JobInterview", "job interview")]
    [InlineData("BusinessEmail", "business email")]
    [InlineData("FreeTalk", "free conversation")]
    public void Each_scenario_adds_its_own_instructions(string scenario, string expected)
    {
        var prompt = AiTutorPromptBuilder.Build(TargetLanguage.English, LanguageLevel.A2, scenario, BaseLanguage.Vietnamese);

        Assert.Contains(expected, prompt, StringComparison.OrdinalIgnoreCase);
        foreach (var other in new[] { "job interview", "business email", "free conversation" }.Where(o => o != expected))
            Assert.DoesNotContain(other, prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(TargetLanguage.English)]
    [InlineData(TargetLanguage.Chinese)]
    public void The_prompt_always_demands_the_four_json_fields(TargetLanguage language)
    {
        var level = language == TargetLanguage.English ? LanguageLevel.A1 : LanguageLevel.Hsk1;

        var prompt = AiTutorPromptBuilder.Build(language, level, "FreeTalk", BaseLanguage.Vietnamese);

        Assert.Contains("JSON", prompt);
        foreach (var field in new[] { "reply_text", "pinyin_or_ipa", "translation", "grammar_tips" })
            Assert.Contains($"\"{field}\"", prompt);
    }

    [Theory]
    [InlineData(BaseLanguage.Vietnamese, "Vietnamese")]
    [InlineData(BaseLanguage.English, "translated into English")]
    public void Translation_and_tips_are_written_in_the_learners_base_language(BaseLanguage baseLanguage, string expected)
    {
        var prompt = AiTutorPromptBuilder.Build(TargetLanguage.Chinese, LanguageLevel.Hsk2, "FreeTalk", baseLanguage);

        Assert.Contains(expected, prompt);
    }

    [Fact]
    public void A_scenario_code_in_any_casing_is_accepted()
    {
        var prompt = AiTutorPromptBuilder.Build(TargetLanguage.English, LanguageLevel.A1, "jobinterview", BaseLanguage.Vietnamese);

        Assert.Contains("job interview", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.True(TutorScenarios.TryNormalize(" businessEMAIL ", out var canonical));
        Assert.Equal("BusinessEmail", canonical);
    }

    [Theory]
    [InlineData("Karaoke")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unknown_scenario_is_refused(string? scenario)
    {
        Assert.False(TutorScenarios.TryNormalize(scenario, out _));
        Assert.Throws<ArgumentException>(() => AiTutorPromptBuilder.Build(TargetLanguage.English, LanguageLevel.A1, scenario!, BaseLanguage.Vietnamese));
    }

    [Theory]
    [InlineData(TargetLanguage.English, LanguageLevel.Hsk3)]
    [InlineData(TargetLanguage.Chinese, LanguageLevel.B2)]
    public void A_level_of_the_other_language_is_refused(TargetLanguage language, LanguageLevel level)
    {
        Assert.Throws<ArgumentException>(() => AiTutorPromptBuilder.Build(language, level, "FreeTalk", BaseLanguage.Vietnamese));
    }
}

public class AiTutorResponseParserTests
{
    [Fact]
    public void Valid_json_maps_to_the_four_fields()
    {
        var response = AiTutorResponseParser.Parse("""{"reply_text":"Hello!","pinyin_or_ipa":"/həˈloʊ/","translation":"Xin chao!","grammar_tips":"Use 'a' before consonants."}""");

        Assert.True(response.IsStructured);
        Assert.Equal("Hello!", response.ReplyText);
        Assert.Equal("/həˈloʊ/", response.PinyinOrIpa);
        Assert.Equal("Xin chao!", response.Translation);
        Assert.Equal("Use 'a' before consonants.", response.GrammarTips);
    }

    [Fact]
    public void Blank_optional_fields_become_null()
    {
        var response = AiTutorResponseParser.Parse("""{"reply_text":"Hi","pinyin_or_ipa":"","translation":"  ","grammar_tips":null}""");

        Assert.True(response.IsStructured);
        Assert.Null(response.PinyinOrIpa);
        Assert.Null(response.Translation);
        Assert.Null(response.GrammarTips);
    }

    [Fact]
    public void Missing_optional_fields_are_fine()
    {
        var response = AiTutorResponseParser.Parse("""{"reply_text":"Hi"}""");

        Assert.True(response.IsStructured);
        Assert.Equal("Hi", response.ReplyText);
    }

    [Fact]
    public void Tips_given_as_a_list_are_joined_by_lines()
    {
        var response = AiTutorResponseParser.Parse("""{"reply_text":"Hi","grammar_tips":["Tip one","Tip two"]}""");

        Assert.Equal("Tip one\nTip two", response.GrammarTips);
    }

    [Fact]
    public void Chinese_text_survives_the_round_trip()
    {
        var response = AiTutorResponseParser.Parse("""{"reply_text":"你好!","pinyin_or_ipa":"nǐ hǎo!","translation":"Xin chao!"}""");

        Assert.Equal("你好!", response.ReplyText);
        Assert.Equal("nǐ hǎo!", response.PinyinOrIpa);
    }

    [Fact]
    public void Json_wrapped_in_a_code_fence_is_still_read()
    {
        var response = AiTutorResponseParser.Parse("```json\n{\"reply_text\":\"Hi\",\"translation\":\"Chao\"}\n```");

        Assert.True(response.IsStructured);
        Assert.Equal("Hi", response.ReplyText);
        Assert.Equal("Chao", response.Translation);
    }

    [Theory]
    [InlineData("Sure! Let's practise. Tell me about your last job.")]
    [InlineData("{ this is not json")]
    [InlineData("[\"a\",\"b\"]")]
    [InlineData("\"just a string\"")]
    public void Anything_that_is_not_the_requested_json_becomes_the_reply_unchanged(string raw)
    {
        var response = AiTutorResponseParser.Parse(raw);

        Assert.False(response.IsStructured);
        Assert.Equal(raw, response.ReplyText);
        Assert.Null(response.PinyinOrIpa);
        Assert.Null(response.Translation);
        Assert.Null(response.GrammarTips);
    }

    [Theory]
    [InlineData("""{"translation":"Chao"}""")]
    [InlineData("""{"reply_text":"   "}""")]
    [InlineData("""{"reply_text":42}""")]
    public void Json_without_a_usable_reply_falls_back_to_the_whole_text(string raw)
    {
        var response = AiTutorResponseParser.Parse(raw);

        Assert.False(response.IsStructured);
        Assert.Equal(raw, response.ReplyText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_output_gives_an_empty_unstructured_reply(string? raw)
    {
        var response = AiTutorResponseParser.Parse(raw);

        Assert.False(response.IsStructured);
        Assert.Equal(string.Empty, response.ReplyText);
    }
}
