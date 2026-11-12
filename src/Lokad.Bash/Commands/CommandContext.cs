using System.Collections.Generic;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Commands;

/// <summary>
/// Owns command cleanup. Typed inheritance is stored inline in
/// the same allocation, without requiring interpreters to specialize command ownership.
/// </summary>
public class CommandContext
{
    public CommandContext(
        IReadOnlyList<FileDescriptor> descriptorsToClose,
        IReadOnlyList<FileDescriptor> borrowedDescriptors)
    {
        DescriptorsToClose = descriptorsToClose;
        BorrowedDescriptors = borrowedDescriptors;
    }

    public IReadOnlyList<FileDescriptor> DescriptorsToClose { get; }
    public IReadOnlyList<FileDescriptor> BorrowedDescriptors { get; }

    public static CommandContext CreateIndependent(
        FileDescriptor stdIn, FileDescriptor stdOut, FileDescriptor stdErr,
        IReadOnlyList<FileDescriptor> borrowedDescriptors) =>
        new(BuildDescriptorsToClose(stdIn, stdOut, stdErr, borrowedDescriptors), borrowedDescriptors);

    public CommandContext CreateChild(
        CommandInvocation parent, FileDescriptor stdIn, FileDescriptor stdOut, FileDescriptor stdErr)
    {
        var borrowedDescriptors = new DescriptorHandleCollector(new List<FileDescriptor>(BorrowedDescriptors));
        borrowedDescriptors.Add(parent.StdIn);
        borrowedDescriptors.Add(parent.StdOut);
        borrowedDescriptors.Add(parent.StdErr);

        var owned = BuildDescriptorsToClose(stdIn, stdOut, stdErr, borrowedDescriptors.Descriptors);
        borrowedDescriptors.Add(stdIn);
        borrowedDescriptors.Add(stdOut);
        borrowedDescriptors.Add(stdErr);
        return CreateChildContext(owned, borrowedDescriptors.Descriptors);
    }

    /// <summary>
    /// Creates a child context with the supplied ownership and borrowed handles.
    /// Typed contexts must retain their inheritance value unchanged; interpreters rebind it when starting.
    /// </summary>
    public virtual CommandContext CreateChildContext(
        IReadOnlyList<FileDescriptor> descriptorsToClose,
        IReadOnlyList<FileDescriptor> borrowedDescriptors) =>
        new(descriptorsToClose, borrowedDescriptors);

    public static IReadOnlyList<FileDescriptor> BuildDescriptorsToClose(
        FileDescriptor stdIn, FileDescriptor stdOut, FileDescriptor stdErr,
        IReadOnlyList<FileDescriptor> borrowedDescriptors)
    {
        var descriptors = new List<FileDescriptor>(3);
        AddDescriptor(stdIn);
        AddDescriptor(stdOut);
        AddDescriptor(stdErr);
        return descriptors;

        void AddDescriptor(FileDescriptor descriptor)
        {
            if (descriptor.Id <= FileDescriptor.StdErr.Id || descriptor.IsDevNull
                || IsBorrowed(descriptor) || descriptors.Contains(descriptor))
                return;
            descriptors.Add(descriptor);
        }

        bool IsBorrowed(FileDescriptor descriptor)
        {
            foreach (var borrowed in borrowedDescriptors)
                if (borrowed == descriptor)
                    return true;
            return false;
        }
    }
}

/// <summary>Stores an interpreter's value alongside command ownership without boxing or a separate allocation.</summary>
public sealed class CommandContext<TInheritance> : CommandContext where TInheritance : struct
{
    public CommandContext(
        IReadOnlyList<FileDescriptor> descriptorsToClose,
        IReadOnlyList<FileDescriptor> borrowedDescriptors,
        TInheritance inheritance)
        : base(descriptorsToClose, borrowedDescriptors)
    {
        Inheritance = inheritance;
    }

    public TInheritance Inheritance { get; }

    /// <inheritdoc />
    public override CommandContext CreateChildContext(
        IReadOnlyList<FileDescriptor> descriptorsToClose,
        IReadOnlyList<FileDescriptor> borrowedDescriptors) =>
        new CommandContext<TInheritance>(descriptorsToClose, borrowedDescriptors, Inheritance);
}
