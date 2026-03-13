namespace CodeMap.Core.Tests.Errors;

using CodeMap.Core.Errors;
using FluentAssertions;

public sealed class ResultTests
{
    [Fact]
    public void Success_IsSuccess_ReturnsTrue() =>
        Result<int, string>.Success(42).IsSuccess.Should().BeTrue();

    [Fact]
    public void Success_IsFailure_ReturnsFalse() =>
        Result<int, string>.Success(42).IsFailure.Should().BeFalse();

    [Fact]
    public void Success_Value_ReturnsValue() =>
        Result<int, string>.Success(42).Value.Should().Be(42);

    [Fact]
    public void Success_Error_ThrowsInvalidOperationException() =>
        FluentActions.Invoking(() => Result<int, string>.Success(42).Error)
            .Should().Throw<InvalidOperationException>();

    [Fact]
    public void Failure_IsSuccess_ReturnsFalse() =>
        Result<int, string>.Failure("err").IsSuccess.Should().BeFalse();

    [Fact]
    public void Failure_IsFailure_ReturnsTrue() =>
        Result<int, string>.Failure("err").IsFailure.Should().BeTrue();

    [Fact]
    public void Failure_Value_ThrowsInvalidOperationException() =>
        FluentActions.Invoking(() => Result<int, string>.Failure("err").Value)
            .Should().Throw<InvalidOperationException>();

    [Fact]
    public void Failure_Error_ReturnsError() =>
        Result<int, string>.Failure("err").Error.Should().Be("err");

    [Fact]
    public void ImplicitConversion_FromValue_CreatesSuccess()
    {
        Result<int, string> r = 42;
        r.IsSuccess.Should().BeTrue();
        r.Value.Should().Be(42);
    }

    [Fact]
    public void Match_OnSuccess_CallsSuccessFunc()
    {
        var result = Result<int, string>.Success(10)
            .Match(v => v * 2, _ => -1);
        result.Should().Be(20);
    }

    [Fact]
    public void Match_OnFailure_CallsFailureFunc()
    {
        var result = Result<int, string>.Failure("oops")
            .Match(_ => 999, e => e.Length);
        result.Should().Be(4); // "oops".Length
    }

    [Fact]
    public void Switch_OnSuccess_CallsSuccessAction()
    {
        int called = 0;
        Result<int, string>.Success(1).Switch(v => called = v, _ => called = -1);
        called.Should().Be(1);
    }

    [Fact]
    public void Switch_OnFailure_CallsFailureAction()
    {
        string? captured = null;
        Result<int, string>.Failure("err").Switch(_ => { }, e => captured = e);
        captured.Should().Be("err");
    }

    [Fact]
    public void Map_OnSuccess_TransformsValue()
    {
        var r = Result<int, string>.Success(5).Map(v => v.ToString());
        r.IsSuccess.Should().BeTrue();
        r.Value.Should().Be("5");
    }

    [Fact]
    public void Map_OnFailure_PassesThroughError()
    {
        var r = Result<int, string>.Failure("err").Map(v => v.ToString());
        r.IsFailure.Should().BeTrue();
        r.Error.Should().Be("err");
    }

    [Fact]
    public void Bind_OnSuccess_AppliesTransform()
    {
        var r = Result<int, string>.Success(5)
            .Bind(v => Result<string, string>.Success(v.ToString()));
        r.IsSuccess.Should().BeTrue();
        r.Value.Should().Be("5");
    }

    [Fact]
    public void Bind_OnFailure_PassesThroughError()
    {
        var r = Result<int, string>.Failure("bad")
            .Bind(v => Result<string, string>.Success(v.ToString()));
        r.IsFailure.Should().BeTrue();
        r.Error.Should().Be("bad");
    }

    [Fact]
    public void Bind_ChainedSuccess_AllTransformsApplied()
    {
        var r = Result<int, string>.Success(2)
            .Bind(v => Result<int, string>.Success(v * 3))
            .Bind(v => Result<string, string>.Success($"result={v}"));
        r.Value.Should().Be("result=6");
    }

    [Fact]
    public void Bind_ChainedWithFailure_ShortCircuits()
    {
        var r = Result<int, string>.Success(2)
            .Bind(_ => Result<int, string>.Failure("step2-failed"))
            .Bind(v => Result<string, string>.Success($"never-{v}"));
        r.IsFailure.Should().BeTrue();
        r.Error.Should().Be("step2-failed");
    }
}
