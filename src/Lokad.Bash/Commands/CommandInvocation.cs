using System;
using System.Collections.Generic;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Commands;

/// <summary> An exported environment variable for a command invocation. </summary>
public readonly record struct EnvironmentVariable(BindingName Name, ShellValue Value);

/// <summary>
/// Represents a single command invocation with resolved arguments and descriptors.
/// </summary>
/// <remarks>Callers must not mutate retained argument/environment/function lists or their byte storage.</remarks>
public sealed class CommandInvocation
{
    private readonly CommandContext _context;
    public CommandContext Context => _context;
    /// <summary>Handles a nested interpreter borrows without taking responsibility for closing them.</summary>
    /// <remarks>The caller must not mutate this retained list. It may include handles owned by this command.</remarks>
    public IReadOnlyList<FileDescriptor> BorrowedDescriptors => _context.BorrowedDescriptors;

    /// <summary> Creates an independent command owning its non-reserved stream handles. </summary>
    /// <param name="commandName">The command name.</param>
    /// <param name="arguments">The byte-valued command arguments.</param>
    /// <param name="currentDirectory">The working directory for relative paths, independent of PWD.</param>
    /// <param name="environment">The exported environment variables.</param>
    /// <param name="stdIn">The standard input descriptor, or null to use <see cref="FileDescriptor.StdIn"/>.</param>
    /// <param name="stdOut">The standard output descriptor, or null to use <see cref="FileDescriptor.StdOut"/>.</param>
    /// <param name="stdErr">The standard error descriptor, or null to use <see cref="FileDescriptor.StdErr"/>.</param>
    public CommandInvocation(
        string commandName,
        IReadOnlyList<ShellValue> arguments,
        ShellPath currentDirectory,
        IReadOnlyList<EnvironmentVariable> environment,
        FileDescriptor? stdIn,
        FileDescriptor? stdOut,
        FileDescriptor? stdErr)
        : this(commandName, arguments, currentDirectory, environment, stdIn, stdOut, stdErr,
            CommandContext.CreateIndependent(stdIn ?? FileDescriptor.StdIn, stdOut ?? FileDescriptor.StdOut,
                stdErr ?? FileDescriptor.StdErr, []))
    {
    }

    /// <summary>Creates an equivalent invocation sharing descriptor ownership.</summary>
    public CommandInvocation WithCommandName(string commandName) =>
        new(commandName, this);

    private CommandInvocation(string commandName, CommandInvocation source)
    {
        CommandName = commandName;
        Arguments = source.Arguments;
        ArgumentZeroOverride = source.ArgumentZeroOverride;
        Environment = source.Environment;
        Functions = source.Functions;
        CurrentDirectory = source.CurrentDirectory;
        StdIn = source.StdIn;
        StdOut = source.StdOut;
        StdErr = source.StdErr;
        _context = source._context;
    }

    public CommandInvocation(
        string commandName,
        IReadOnlyList<ShellValue> arguments,
        ShellPath currentDirectory,
        IReadOnlyList<EnvironmentVariable> environment,
        FileDescriptor? stdIn,
        FileDescriptor? stdOut,
        FileDescriptor? stdErr,
        CommandContext context)
    {
        CommandName = commandName;
        Arguments = arguments;
        Environment = environment;
        CurrentDirectory = currentDirectory;
        StdIn = stdIn ?? FileDescriptor.StdIn;
        StdOut = stdOut ?? FileDescriptor.StdOut;
        StdErr = stdErr ?? FileDescriptor.StdErr;
        _context = context;
    }

    /// <summary>Creates command ownership and carries an interpreter's value without boxing.</summary>
    /// <remarks>
    /// borrowedDescriptors excludes caller-owned handles from command cleanup. nestedBorrowedDescriptors
    /// also includes handles owned by this command that a nested interpreter must leave open.
    /// Neither retained descriptor list may be mutated by callers. Use immutable snapshots for inherited state;
    /// child commands preserve inheritance while retaining their own descriptor ownership.
    /// </remarks>
    public static CommandInvocation CreateWithInheritance<TInheritance>(
        string commandName,
        IReadOnlyList<ShellValue> arguments,
        ShellPath currentDirectory,
        IReadOnlyList<EnvironmentVariable> environment,
        FileDescriptor stdIn,
        FileDescriptor stdOut,
        FileDescriptor stdErr,
        IReadOnlyList<FileDescriptor> borrowedDescriptors,
        IReadOnlyList<FileDescriptor> nestedBorrowedDescriptors,
        TInheritance inheritance,
        ShellValue? argumentZero,
        IReadOnlyList<ExportedFunction> functions) where TInheritance : struct
    {
        return new(commandName, arguments, currentDirectory, environment, stdIn, stdOut, stdErr,
            new CommandContext<TInheritance>(CommandContext.BuildDescriptorsToClose(stdIn, stdOut, stdErr, borrowedDescriptors),
                nestedBorrowedDescriptors, inheritance))
        { ArgumentZeroOverride = argumentZero, Functions = functions };
    }

    /// <summary>Reads the interpreter's value without exposing command ownership.</summary>
    public bool TryGetInheritance<TInheritance>(out TInheritance inheritance) where TInheritance : struct
    {
        if (_context is CommandContext<TInheritance> context)
        {
            inheritance = context.Inheritance;
            return true;
        }
        inheritance = default;
        return false;
    }

    /// <summary>
    /// Creates an independent command that borrows descriptors.
    /// </summary>
    /// <remarks>
    /// Borrowed descriptors are passed to the command for use, but their lifetime remains owned by the caller.
    /// The host must not close them when this command exits. Non-standard descriptors used by the command that
    /// are not listed as borrowed remain command-owned and are closed after execution.
    /// </remarks>
    public static CommandInvocation CreateWithBorrowedDescriptors(
        string commandName,
        IReadOnlyList<ShellValue> arguments,
        ShellPath currentDirectory,
        IReadOnlyList<EnvironmentVariable> environment,
        FileDescriptor stdIn,
        FileDescriptor stdOut,
        FileDescriptor stdErr,
        IReadOnlyList<FileDescriptor> borrowedDescriptors) =>
        new(
            commandName,
            arguments,
            currentDirectory,
            environment,
            stdIn,
            stdOut,
            stdErr,
            CommandContext.CreateIndependent(stdIn, stdOut, stdErr, borrowedDescriptors));

    /// <summary>
    /// Creates a command on the same host, borrowing parent handles and preserving interpreter inheritance.
    /// The child retains the parent's working directory and exported definitions.
    /// </summary>
    public CommandInvocation CreateChild(
        string childCommandName,
        IReadOnlyList<ShellValue> childArguments,
        IReadOnlyList<EnvironmentVariable> childEnvironment,
        FileDescriptor childStdIn,
        FileDescriptor childStdOut,
        FileDescriptor childStdErr) =>
        new(childCommandName, childArguments, CurrentDirectory, childEnvironment, childStdIn, childStdOut, childStdErr,
            _context.CreateChild(this, childStdIn, childStdOut, childStdErr)) { Functions = Functions };

    /// <summary> Gets the command name. </summary>
    public string CommandName { get; }

    /// <summary>An explicit argv[0] for this invocation; null keeps the tool's default, while empty is a valid override.</summary>
    public ShellValue? ArgumentZeroOverride { get; init; }

    /// <summary> Gets the byte-valued command arguments. </summary>
    public IReadOnlyList<ShellValue> Arguments { get; }

    /// <summary>Serialized definitions for hosted child shells, separate from the scalar environment.</summary>
    public IReadOnlyList<ExportedFunction> Functions { get; init; } = [];

    /// <summary>Decodes arguments for text-only tools; the consuming host owns the decoded copies.</summary>
    public bool TryGetTextArguments(out IReadOnlyList<string> arguments, out ReadOnlyMemory<byte> error)
    {
        var decoded = new List<string>();
        foreach (var value in Arguments)
        {
            if (!TryDecodeArgument(value, out var text, out error))
            {
                arguments = [];
                return false;
            }
            decoded.Add(text);
        }
        arguments = decoded;
        error = ReadOnlyMemory<byte>.Empty;
        return true;
    }

    /// <summary>Requires UTF-8; the consuming host owns and admits the decoded storage.</summary>
    public bool TryDecodeArgument(ShellValue value, out string text, out ReadOnlyMemory<byte> error)
    {
        text = string.Empty;
        if (!value.TryGetUtf8Text(out text))
        {
            error = "shell: text argument must be valid UTF-8\n"u8.ToArray();
            return false;
        }
        error = ReadOnlyMemory<byte>.Empty;
        return true;
    }

    /// <summary>Reuses cached name text and decodes values at a text-only host boundary.</summary>
    public bool TryDecodeEnvironmentVariable(EnvironmentVariable variable,
        out string name, out string value, out ReadOnlyMemory<byte> error)
    {
        value = string.Empty;
        if (!variable.Name.TryGetText(out name)
            && !TryDecodeArgument(variable.Name.ToValue(), out name, out error))
            return false;
        return TryDecodeArgument(variable.Value, out value, out error);
    }

    /// <summary> Gets the exported environment variables. </summary>
    public IReadOnlyList<EnvironmentVariable> Environment { get; }

    /// <summary> Gets the working directory used for path resolution, independently of the environment. </summary>
    public ShellPath CurrentDirectory { get; }

    /// <summary> Gets the standard input descriptor. </summary>
    public FileDescriptor StdIn { get; }

    /// <summary> Gets the standard output descriptor. </summary>
    public FileDescriptor StdOut { get; }

    /// <summary> Gets the standard error descriptor. </summary>
    public FileDescriptor StdErr { get; }

    /// <summary> Gets the descriptors owned by this command invocation and closed after it exits. </summary>
    public IReadOnlyList<FileDescriptor> DescriptorsToClose => _context.DescriptorsToClose;
}
