namespace CodeMap.TestUtilities.Builders;

using CodeMap.Core.Enums;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.TestUtilities.Fixtures;

/// <summary>
/// Fluent builder for creating ResponseEnvelope&lt;T&gt; instances in tests.
/// </summary>
public class ResponseEnvelopeBuilder<T>
{
    private string _answer = "Test answer";
    private T _data = default!;
    private List<EvidencePointer> _evidence = [];
    private List<NextAction> _nextActions = [];
    private Confidence _confidence = Confidence.High;
    private ResponseMeta _meta = new(
        new TimingBreakdown(1.0),
        TestConstants.SampleCommitSha,
        new Dictionary<string, LimitApplied>(),
        0L, 0m);

    public ResponseEnvelopeBuilder<T> WithAnswer(string answer) { _answer = answer; return this; }
    public ResponseEnvelopeBuilder<T> WithData(T data) { _data = data; return this; }
    public ResponseEnvelopeBuilder<T> WithConfidence(Confidence c) { _confidence = c; return this; }
    public ResponseEnvelopeBuilder<T> WithMeta(ResponseMeta meta) { _meta = meta; return this; }

    public ResponseEnvelope<T> Build() =>
        new(_answer, _data, _evidence, _nextActions, _confidence, _meta);
}
