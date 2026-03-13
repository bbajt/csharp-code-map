namespace CodeMap.Integration.Tests.Regression;

using CodeMap.Integration.Tests.Workflows;

/// <summary>
/// xUnit collection fixture so all Regression test classes share a single
/// IndexedSampleSolutionFixture instance (one Roslyn compilation for all ~30 tests).
/// </summary>
[CollectionDefinition("Regression")]
public sealed class RegressionCollection : ICollectionFixture<IndexedSampleSolutionFixture> { }
