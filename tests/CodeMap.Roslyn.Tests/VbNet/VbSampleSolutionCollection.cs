namespace CodeMap.Roslyn.Tests.VbNet;

/// <summary>
/// xUnit collection so all VB.NET extraction test classes share a single
/// VbSampleSolutionFixture (one Roslyn compilation for all VB tests).
/// </summary>
[CollectionDefinition("VbSampleSolution")]
public sealed class VbSampleSolutionCollection : ICollectionFixture<VbSampleSolutionFixture> { }
