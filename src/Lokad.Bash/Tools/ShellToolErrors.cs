using System;
using System.Threading;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Tools;

/// <summary>Formats command/path diagnostics with the host's error detail.</summary>
public static class ShellToolErrors
{
    private const string MissingPathDetail = "No such file or directory\n";

    /// <summary>Writes a path diagnostic, keeping the formatted record alive through host write completion.</summary>
    /// <exception cref="MemoryBudgetExceededException">The host budget refused the diagnostic buffer.</exception>
    public static async Task<int> WritePathErrorAsync(
        IBashHost host,
        FileDescriptor standardError,
        string commandName,
        ReadOnlyMemory<byte> displayPath,
        OperationError? error,
        CancellationToken cancellationToken)
    {
        using var diagnostic = BuildDiagnostic(commandName ?? string.Empty, displayPath, destination: null,
            error?.Message ?? MissingPathDetail, host.MemoryBudget);
        return (await host.WriteAsync(
            standardError,
            diagnostic.ToMemory(),
            cancellationToken).ConfigureAwait(false)).IsError ? 1 : 0;
    }

    public static ReadOnlyMemory<byte> FormatPathError(string commandName, ReadOnlyMemory<byte> displayPath, OperationError? error) =>
        FormatDiagnostic(commandName ?? string.Empty, displayPath, destination: null, error?.Message ?? MissingPathDetail);

    /// <summary>Formats a read failure, omitting the operand prefix for unnamed stdin.</summary>
    public static ReadOnlyMemory<byte> FormatReadError(string commandName, ReadOnlyMemory<byte> displayPath, OperationError error) =>
        displayPath.IsEmpty
            ? FormatDiagnostic(commandName ?? string.Empty, displayPath: null, destination: null, error.Message)
            : FormatPathError(commandName, displayPath, error);

    /// <summary>Formats an operand diagnostic when no command owns the failure, such as a redirection.</summary>
    public static ReadOnlyMemory<byte> FormatPathError(ReadOnlyMemory<byte> displayPath, OperationError? error) =>
        FormatDiagnostic(commandName: null, displayPath, destination: null, error?.Message ?? MissingPathDetail);

    /// <summary>Formats the attributed operand, or both operands when the failure is operation-wide.</summary>
    public static ReadOnlyMemory<byte> FormatTransferError(
        string commandName, ReadOnlyMemory<byte> source, ReadOnlyMemory<byte> destination, FileTransferError failure) =>
        failure.Operand switch
        {
            FileTransferOperand.Source => FormatPathError(commandName, source, failure.Error),
            FileTransferOperand.Destination => FormatPathError(commandName, destination, failure.Error),
            FileTransferOperand.Operation => FormatDiagnostic(commandName ?? string.Empty, source, destination,
                failure.Error.Message ?? MissingPathDetail),
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        };

    private static ReadOnlyMemory<byte> FormatDiagnostic(string? commandName, ReadOnlyMemory<byte>? displayPath,
        ReadOnlyMemory<byte>? destination, string? detail)
    {
        using var diagnostic = BuildDiagnostic(commandName, displayPath, destination, detail, budget: null);
        return diagnostic.ToMemory();
    }

    private static ByteMemoryBuilder BuildDiagnostic(string? commandName, ReadOnlyMemory<byte>? displayPath,
        ReadOnlyMemory<byte>? destination, string? detail, IMemoryBudget? budget)
    {
        var encoding = Utf8Text.StrictUtf8;
        ReadOnlySpan<byte> fieldSeparator = ": "u8;
        ReadOnlySpan<byte> transferSeparator = " -> "u8;
        var commandLength = encoding.GetByteCount(commandName.AsSpan());
        var detailLength = encoding.GetByteCount(detail.AsSpan());
        var length = (long)commandLength + detailLength
            + (commandName is null ? 0 : fieldSeparator.Length)
            + (displayPath is { } path ? (long)path.Length + fieldSeparator.Length : 0)
            + (destination is { } target ? (long)target.Length + transferSeparator.Length : 0);
        if (length > ByteMemoryBuilder.MaxMemoryBuilderBytes)
            throw new ByteMemoryBuilder.LimitExceededException();

        var diagnostic = budget is not null && length > ByteMemoryBuilder.DefaultCapacity
            ? new ByteMemoryBuilder((int)length, budget)
            : ByteMemoryBuilder.CreateUnaccounted((int)length);
        try
        {
            if (commandName is not null)
            {
                encoding.GetBytes(commandName.AsSpan(), diagnostic.AppendSpan(commandLength));
                diagnostic.Append(fieldSeparator);
            }
            if (displayPath is { } operand)
            {
                diagnostic.Append(operand);
                if (destination is { } second)
                    diagnostic.Append(transferSeparator).Append(second);
                diagnostic.Append(fieldSeparator);
            }
            encoding.GetBytes(detail.AsSpan(), diagnostic.AppendSpan(detailLength));
            return diagnostic;
        }
        catch
        {
            diagnostic.Dispose();
            throw;
        }
    }
}
