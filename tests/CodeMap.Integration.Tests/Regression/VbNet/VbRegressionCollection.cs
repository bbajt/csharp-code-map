namespace CodeMap.Integration.Tests.Regression.VbNet;

using CodeMap.Integration.Tests.Workflows;

/// <summary>
/// xUnit collection fixture so all VB.NET regression test classes share a single
/// IndexedSampleVbSolutionFixture instance (one Roslyn compilation for all VB tests).
/// </summary>
[CollectionDefinition("VbRegression")]
public sealed class VbRegressionCollection : ICollectionFixture<IndexedSampleVbSolutionFixture> { }
