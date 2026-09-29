namespace CodeMap.Mcp.Tests.Handlers;

using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Mcp.Handlers;
using FluentAssertions;

/// <summary>
/// Tests for <see cref="HandlerHelpers.ClassifyException"/> and
/// <see cref="HandlerHelpers.IsSharingViolation"/> — mapping handler exceptions to honest,
/// retryable error codes (PHASE-21-02 T02, ADR-041).
/// </summary>
public sealed class HandlerHelpersTests
{
    private const int WinSharingViolation = unchecked((int)0x80070020);
    private const int WinLockViolation = unchecked((int)0x80070021);

    private static IOException SharingViolation() =>
        OperatingSystem.IsWindows()
            ? new IOException("The process cannot access the file 'overlay.wal' because it is being used by another process.", WinSharingViolation)
            : new IOException("The process cannot access the file 'overlay.wal' because it is being used by another process.",
                OperatingSystem.IsMacOS() ? 35 : 11);

    [Fact]
    public void ClassifyException_SharingViolation_WorkspaceScoped_WorkspaceInUseRetryable()
    {
        var err = HandlerHelpers.ClassifyException(SharingViolation(), "workspace_create", "agent-1");

        err.Code.Should().Be(ErrorCodes.WorkspaceInUse);
        err.Retryable.Should().BeTrue();
        err.Message.Should().Contain("agent-1").And.Contain("overlay.wal");
    }

    [Fact]
    public void ClassifyException_SharingViolation_NotWorkspaceScoped_StorageError()
    {
        var err = HandlerHelpers.ClassifyException(SharingViolation(), "index_ensure_baseline", null);

        err.Code.Should().Be(ErrorCodes.StorageError);
        err.Retryable.Should().BeTrue();
    }

    [Fact]
    public void ClassifyException_PlainIo_StorageErrorRetryable()
    {
        var err = HandlerHelpers.ClassifyException(new IOException("disk full"), "index_refresh_overlay", "ws");

        err.Code.Should().Be(ErrorCodes.StorageError);
        err.Retryable.Should().BeTrue();
    }

    [Fact]
    public void ClassifyException_UnauthorizedAccess_StorageError() =>
        HandlerHelpers.ClassifyException(new UnauthorizedAccessException("denied"), "index_cleanup", null)
            .Code.Should().Be(ErrorCodes.StorageError);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ClassifyException_StorageFailure_StorageErrorRetryable(bool transient)
    {
        var err = HandlerHelpers.ClassifyException(new FakeStorageFailure("segment gone", transient), "index_ensure_baseline", null);

        err.Code.Should().Be(ErrorCodes.StorageError);
        err.Retryable.Should().BeTrue();
    }

    [Fact]
    public void ClassifyException_TransientStorageFailure_MessageSuggestsRetry() =>
        HandlerHelpers.ClassifyException(new FakeStorageFailure("busy", transient: true), "index_ensure_baseline", null)
            .Message.Should().ContainEquivalentOf("retry");

    [Fact]
    public void ClassifyException_Unexpected_InternalErrorNotRetryable()
    {
        var err = HandlerHelpers.ClassifyException(new InvalidOperationException("null ref somewhere"), "workspace_list", null);

        err.Code.Should().Be(ErrorCodes.InternalError);
        err.Retryable.Should().BeFalse();
    }

    [Fact]
    public void ClassifyException_ArgumentExceptionFromValueGuard_InvalidArgument()
    {
        // e.g. WorkspaceId.From("../x") throws inside the handler's try — still a caller error.
        var err = HandlerHelpers.ClassifyException(new ArgumentException("WorkspaceId must not contain '..'"), "workspace_create", "../x");

        err.Code.Should().Be(ErrorCodes.InvalidArgument);
        err.Retryable.Should().BeFalse();
    }

    [Theory]
    [InlineData("workspace_create", "ws")]
    [InlineData("index_ensure_baseline", null)]
    public void ClassifyException_KeepsOperationAndOriginalMessage(string operation, string? workspaceId)
    {
        var err = HandlerHelpers.ClassifyException(new InvalidOperationException("the original detail"), operation, workspaceId);

        err.Message.Should().Contain(operation).And.Contain("the original detail");
    }

    [Fact]
    public void IsSharingViolation_WindowsHResults_True()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows HRESULTs");
        HandlerHelpers.IsSharingViolation(new IOException("x", WinSharingViolation)).Should().BeTrue();
        HandlerHelpers.IsSharingViolation(new IOException("x", WinLockViolation)).Should().BeTrue();
    }

    [Fact]
    public void IsSharingViolation_OtherIo_False() =>
        HandlerHelpers.IsSharingViolation(new IOException("disk full", unchecked((int)0x80070070))).Should().BeFalse();

    [Fact]
    public void IsSharingViolation_RealLockedFile_True()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codemap-lock-{Guid.NewGuid():N}.wal");
        try
        {
            using var holder = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            var ex = Record.Exception(() => new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read).Dispose());

            ex.Should().BeOfType<IOException>();
            HandlerHelpers.IsSharingViolation((IOException)ex!).Should().BeTrue($"HResult 0x{ex!.HResult:X8}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── ClassifyUnhandled (tools/call boundary, PHASE-21-04 T02, ADR-045) ─────────

    [Fact]
    public void ClassifyUnhandled_ArgumentException_IsInternalError_UnlikeClassifyException()
    {
        var ex = new ArgumentException("Value cannot be empty.");

        HandlerHelpers.ClassifyException(ex, "symbols_search", null).Code.Should().Be(ErrorCodes.InvalidArgument,
            "inside a handler's try, value-type guards on caller input are caller errors");
        var boundary = HandlerHelpers.ClassifyUnhandled(ex, "symbols_search", null);
        boundary.Code.Should().Be(ErrorCodes.InternalError,
            "an argument exception that escaped the handler is a defect, not bad input");
        boundary.Details!["exception_type"].Should().Be("System.ArgumentException");
    }

    [Fact]
    public void ClassifyUnhandled_SharingViolation_WithWorkspace_IsWorkspaceInUse()
    {
        var error = HandlerHelpers.ClassifyUnhandled(SharingViolation(), "refs_find", "session");

        error.Code.Should().Be(ErrorCodes.WorkspaceInUse);
        error.Retryable.Should().BeTrue();
        error.Details.Should().NotContainKey("exception_type");
    }

    [Fact]
    public void ClassifyUnhandled_StorageFailure_IsStorageError_WithoutExceptionType()
    {
        var error = HandlerHelpers.ClassifyUnhandled(new FakeStorageFailure("incomplete baseline", transient: false), "graph_callers", null);

        error.Code.Should().Be(ErrorCodes.StorageError);
        error.Message.Should().Contain("incomplete baseline");
        (error.Details?.ContainsKey("exception_type") ?? false).Should().BeFalse();
    }

    [Fact]
    public void ClassifyUnhandled_Unexpected_IsInternalError_WithExceptionType()
    {
        var error = HandlerHelpers.ClassifyUnhandled(new NullReferenceException("x"), "types_hierarchy", null);

        error.Code.Should().Be(ErrorCodes.InternalError);
        error.Retryable.Should().BeFalse();
        error.Details!["exception_type"].Should().Be("System.NullReferenceException");
    }

    private sealed class FakeStorageFailure(string message, bool transient) : Exception(message), IStorageFailure
    {
        public bool IsTransient { get; } = transient;
    }
}
