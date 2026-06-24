using System.Collections.Generic;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public class FastestModelSelectionTests
{
    private static (string Id, double? Multiplier) M(string id, double? multiplier = null) => (id, multiplier);

    [Fact]
    public void Prefers_Gpt54Mini_Even_When_Not_The_Cheapest()
    {
        var models = new List<(string Id, double? Multiplier)>
        {
            M("claude-opus-4.8", 10),
            M("gpt-5-mini", 0),       // cheaper, but lower preference
            M("gpt-5.4-mini", 1),     // top preference wins
            M("gemini-3.5-flash", 0),
        };

        var result = CopilotService.SelectFastestModelId(models, CopilotService.PreferredFastModelIds);

        Assert.Equal("gpt-5.4-mini", result);
    }

    [Fact]
    public void Respects_Preference_Order_When_Top_Choice_Missing()
    {
        var models = new List<(string Id, double? Multiplier)>
        {
            M("claude-opus-4.8", 10),
            M("gemini-3.5-flash", 5),
            M("gpt-5-mini", 7),
        };

        var result = CopilotService.SelectFastestModelId(models, CopilotService.PreferredFastModelIds);

        Assert.Equal("gpt-5-mini", result);
    }

    [Fact]
    public void Matches_Preferred_Id_Case_Insensitively_And_Returns_Canonical_Casing()
    {
        var models = new List<(string Id, double? Multiplier)>
        {
            M("GPT-5.4-Mini", 3),
        };

        var result = CopilotService.SelectFastestModelId(models, CopilotService.PreferredFastModelIds);

        Assert.Equal("GPT-5.4-Mini", result);
    }

    [Fact]
    public void Falls_Back_To_Lowest_Billing_Multiplier_When_No_Preferred()
    {
        var models = new List<(string Id, double? Multiplier)>
        {
            M("model-expensive", 9),
            M("model-cheap", 1),
            M("model-mid", 4),
        };

        var result = CopilotService.SelectFastestModelId(models, CopilotService.PreferredFastModelIds);

        Assert.Equal("model-cheap", result);
    }

    [Fact]
    public void Falls_Back_To_Lightweight_Name_When_No_Billing_Info()
    {
        var models = new List<(string Id, double? Multiplier)>
        {
            M("big-model"),
            M("some-flash-model"),
        };

        var result = CopilotService.SelectFastestModelId(models, CopilotService.PreferredFastModelIds);

        Assert.Equal("some-flash-model", result);
    }

    [Fact]
    public void Falls_Back_To_First_Model_As_Last_Resort()
    {
        var models = new List<(string Id, double? Multiplier)>
        {
            M("only-model-a"),
            M("only-model-b"),
        };

        var result = CopilotService.SelectFastestModelId(models, CopilotService.PreferredFastModelIds);

        Assert.Equal("only-model-a", result);
    }

    [Fact]
    public void Returns_Null_For_Empty_List()
    {
        var result = CopilotService.SelectFastestModelId(
            new List<(string Id, double? Multiplier)>(), CopilotService.PreferredFastModelIds);

        Assert.Null(result);
    }

    [Fact]
    public void Gpt54Mini_Is_The_Top_Preference()
    {
        Assert.Equal("gpt-5.4-mini", CopilotService.PreferredFastModelIds[0]);
    }
}
