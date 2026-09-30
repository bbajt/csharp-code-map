namespace CodeMap.Integration.Tests.Harness;

using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Harness.Comparison;
using FluentAssertions;

/// <summary>
/// Golden-form normalization. PHASE-21-13 T01: text-search goldens ignore matches in build
/// output (<c>obj/</c>). The Razor generator's files there embed the checkout's absolute path
/// and the source's line endings, so a golden holding them passes on one machine only.
/// </summary>
public class ResultNormalizerTests
{
    [Fact]
    public void FromTextSearch_MatchesUnderObj_AreNotPartOfTheGoldenForm()
    {
        var response = new SearchTextResponse(
            Pattern: "Counter",
            Matches:
            [
                new TextMatch(FilePath.From("App/Components/Pages/Counter.razor"), 4, "<h1>Counter</h1>"),
                new TextMatch(FilePath.From("App/obj/Debug/net10.0/Gen/Counter_razor.g.cs"), 1,
                    "#pragma checksum \"C:\\Users\\someone\\App\\Components\\Pages\\Counter.razor\""),
                new TextMatch(FilePath.From("obj/Debug/net10.0/Gen/Home_razor.g.cs"), 9, "Counter"),
            ],
            TotalFiles: 3,
            Truncated: false);

        var normalized = ResultNormalizer.FromTextSearch(response);

        normalized.FactKeys.Should().Equal("App/Components/Pages/Counter.razor:4:<h1>Counter</h1>");
        normalized.ScalarFields["match_count"].Should().Be("1");
        normalized.TotalAvailable.Should().Be(1);
    }

    [Fact]
    public void FromTextSearch_PathMerelyContainingObj_IsKept()
    {
        var response = new SearchTextResponse(
            Pattern: "x",
            Matches: [new TextMatch(FilePath.From("src/objects/ObjReader.cs"), 2, "x")],
            TotalFiles: 1,
            Truncated: false);

        ResultNormalizer.FromTextSearch(response).FactKeys.Should().ContainSingle();
    }
}
