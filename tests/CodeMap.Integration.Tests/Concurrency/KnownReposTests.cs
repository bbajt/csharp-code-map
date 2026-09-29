namespace CodeMap.Integration.Tests.Concurrency;

using CodeMap.Harness.Repos;
using FluentAssertions;

/// <summary>
/// Unit tests for <see cref="KnownRepos.ForNames(IReadOnlyList{string}, IReadOnlyList{RepoDescriptor})"/>
/// (PHASE-21-06 T01). Configured repos are injected, so the developer's harness config is never read.
/// </summary>
public class KnownReposTests
{
    private static readonly RepoDescriptor EShop = Configured("eShopOnWeb", RepoTier.Medium);
    private static readonly RepoDescriptor Nop = Configured("nopCommerce", RepoTier.Large);

    [Fact]
    public void ForNames_IsCaseInsensitive_AndCoversCommittedAndConfigured()
    {
        var (repos, error) = KnownRepos.ForNames(["ESHOPONWEB", "samplesolution"], [EShop, Nop]);

        error.Should().BeNull();
        repos!.Select(r => r.Name).Should().Equal("eShopOnWeb", "SampleSolution");
    }

    [Fact]
    public void ForNames_PreservesTheOrderGiven()
    {
        var (repos, _) = KnownRepos.ForNames(["nopCommerce", "SampleBlazorSolution", "eShopOnWeb"], [EShop, Nop]);

        repos!.Select(r => r.Name).Should().Equal("nopCommerce", "SampleBlazorSolution", "eShopOnWeb");
    }

    [Fact]
    public void ForNames_UnknownName_ErrorListsEveryKnownName()
    {
        var (repos, error) = KnownRepos.ForNames(["Bitwarden"], [EShop, Nop]);

        repos.Should().BeNull();
        error.Should().Contain("Bitwarden")
            .And.Contain("SampleSolution").And.Contain("SampleVbSolution").And.Contain("SampleBlazorSolution")
            .And.Contain("eShopOnWeb").And.Contain("nopCommerce");
    }

    private static RepoDescriptor Configured(string name, RepoTier tier) => new(
        Name: name,
        SolutionPath: Path.Combine(Path.GetTempPath(), name, name + ".sln"),
        Tier: tier,
        Anchors: [],
        CountExpectation: new IndexCountExpectation(0, long.MaxValue, 0, long.MaxValue, 0),
        KnownQueryInputs: []);
}
