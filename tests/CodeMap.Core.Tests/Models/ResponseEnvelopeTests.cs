namespace CodeMap.Core.Tests.Models;

using CodeMap.Core.Enums;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;

public sealed class ResponseEnvelopeTests
{
    [Fact]
    public void ResponseEnvelope_IsGeneric_CanWrapAnyType()
    {
        var meta = new ResponseMeta(
            new TimingBreakdown(5.0),
            CommitSha.From("aabbccddee00112233445566778899aabbccddee"),
            new Dictionary<string, LimitApplied>(),
            100L,
            0.001m);

        var envelope = new ResponseEnvelope<string>(
            "Answer text",
            "some-data",
            [],
            [],
            Confidence.High,
            meta);

        envelope.Answer.Should().Be("Answer text");
        envelope.Data.Should().Be("some-data");
        envelope.Confidence.Should().Be(Confidence.High);
    }

    [Fact]
    public void ResponseEnvelope_WithIntData_CompilesAndWorks()
    {
        var meta = new ResponseMeta(
            new TimingBreakdown(1.0),
            CommitSha.From("aabbccddee00112233445566778899aabbccddee"),
            new Dictionary<string, LimitApplied>(),
            0L,
            0m);

        var envelope = new ResponseEnvelope<int>("answer", 42, [], [], Confidence.Low, meta);
        envelope.Data.Should().Be(42);
    }
}
