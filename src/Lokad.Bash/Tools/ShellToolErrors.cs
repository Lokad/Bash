using System;
using System.Threading;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Tools;

/// <summary>Formats command/path diagnostics with the host's error detail.</summary>
public static class ShellToolErrors
{
    public static async Task<int> WritePathErrorAsync(
        IBashHost host,
        FileDescriptor standardError,
        string commandName,
        ReadOnlyMemory<byte> displayPath,
        OperationError? error,
        CancellationToken cancellationToken)
    {
        return (await host.WriteAsync(
            standardError,
            FormatPathError(commandName, displayPath, error),
            cancellationToken).ConfigureAwait(false)).IsError ? 1 : 0;
    }

    public static ReadOnlyMemory<byte> FormatPathError(string commandName, ReadOnlyMemory<byte> displayPath, OperationError? error) =>
        FormatPathError(ByteMemoryBuilder.CreateUnaccounted(0).Append(Utf8Text.Encode(commandName)).Append(": "u8), displayPath, error);

    /// <summary>Formats a read failure, omitting the operand prefix for unnamed stdin.</summary>
    public static ReadOnlyMemory<byte> FormatReadError(string commandName, ReadOnlyMemory<byte> displayPath, OperationError error) =>
        displayPath.IsEmpty
            ? ByteMemoryBuilder.CreateUnaccounted(0).Append(Utf8Text.Encode(commandName)).Append(": "u8)
                .Append(Utf8Text.Encode(error.Message)).ToMemory()
            : FormatPathError(commandName, displayPath, error);

    /// <summary>Formats an operand diagnostic when no command owns the failure, such as a redirection.</summary>
    public static ReadOnlyMemory<byte> FormatPathError(ReadOnlyMemory<byte> displayPath, OperationError? error) =>
        FormatPathError(ByteMemoryBuilder.CreateUnaccounted(0), displayPath, error);

    private static ReadOnlyMemory<byte> FormatPathError(ByteMemoryBuilder diagnostic, ReadOnlyMemory<byte> displayPath, OperationError? error) =>
        diagnostic.Append(displayPath).Append(": "u8)
            .Append(Utf8Text.Encode(error?.Message ?? "No such file or directory\n")).ToMemory();

    /// <summary>Formats the attributed operand, or both operands when the failure is operation-wide.</summary>
    public static ReadOnlyMemory<byte> FormatTransferError(
        string commandName, ReadOnlyMemory<byte> source, ReadOnlyMemory<byte> destination, FileTransferError failure) =>
        FormatPathError(commandName, failure.Operand switch
        {
            FileTransferOperand.Source => source,
            FileTransferOperand.Destination => destination,
            FileTransferOperand.Operation => ByteMemoryBuilder.CreateUnaccounted(0).Append(source).Append(" -> "u8).Append(destination).ToMemory(),
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        }, failure.Error);
}
