using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lokad.Bash.Commands;
using Lokad.Bash.Memory;
using Lokad.Bash.Files;
namespace Lokad.Bash;

/// <summary>
/// Executes evaluation operations and returns their results.
/// </summary>
/// <remarks>
/// Concrete implementations are provided by consumers of this library, per requirements.
/// The evaluator manages logical descriptor aliases and their lifetimes; aliases may share one host handle.
/// Members can be invoked concurrently while pipeline stages stream data through shared descriptors.
/// Implementations must allow overlapping producer and consumer operations to make progress; in particular,
/// they must not serialize all calls behind one lock that remains held while awaiting descriptor I/O.
/// </remarks>
public interface IBashHost
{
    /// <summary>The host ownership budget, sharing the process allowance with other scopes.</summary>
    IMemoryBudget MemoryBudget { get; }

    /// <summary>Clocks and CPU accounting used by the time reserved word.</summary>
    IShellTiming Timing { get; }

    /// <summary>
    /// Optional monitored execution scopes bound to this host's descriptors. Scope creation
    /// registers pipe writers; keep each scope through execution and descriptor cleanup.
    /// </summary>
    ShellExecutionContext? Execution { get; }

    /// <summary>
    /// Resolves command names using the host's command registry or search path.
    /// </summary>
    /// <param name="commandNames">The command names to resolve.</param>
    /// <param name="currentDirectory">The working directory for relative command paths, independent of PWD.</param>
    /// <param name="environment">The environment that would be passed to a command.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// Exactly one lookup result per requested command name, in the same order.
    /// </returns>
    /// <remarks>
    /// Missing denotes established unavailability. Expected lookup failures return Failed for that operand
    /// and do not prevent resolving other names. Preserve provider detail and known OperationError.Kind values
    /// without command or operand prefixes; callers own diagnostics and exit status. Release probe resources
    /// before completion. Cancellation and unexpected exceptions propagate. Found does not guarantee a later launch.
    /// </remarks>
    Task<IReadOnlyList<CommandLookupResult>> ResolveCommandsAsync(
        IReadOnlyList<string> commandNames,
        ShellPath currentDirectory,
        IReadOnlyList<EnvironmentVariable> environment,
        CancellationToken cancellationToken);

    /// <summary>Executes one command, preserving launch failures separately from its exit code.</summary>
    /// <param name="command">The command invocation to execute.</param>
    /// <param name="failureReporting">Who reports launch failures; tool-emitted stderr is never suppressed.</param>
    /// <param name="cancellationToken">Cancellation aborts execution, without manufacturing a command result.</param>
    /// <returns>The command's execution result, including launch failures.</returns>
    /// <remarks>
    /// Callers control concurrency through overlapping calls. Command-owned descriptors must be closed before
    /// completion, including on failure or cancellation. Host-reported launch diagnostics must precede cleanup.
    /// Caller-reported failures require the caller to retain its diagnostic descriptors; they do not transfer
    /// ownership of command descriptors. Do not infer launch failure from a tool's numeric exit code.
    /// Direct-script open, read and pre-execution close failures return NotExecutable with their original
    /// OperationError detail and kind. NotFound requires established command or path absence.
    /// Admit retained argument copies through the shared memory budget before copying. Keep their owner
    /// alive until all command users finish; release it after completion, cancellation and cleanup.
    /// </remarks>
    Task<CommandExecutionResult> RunCommandAsync(
        CommandInvocation command,
        CommandLaunchFailureReporting failureReporting,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes bytes to a descriptor and reports whether it can accept more content.
    /// </summary>
    /// <param name="fileDescriptor">The descriptor to write to.</param>
    /// <param name="content">The bytes to write.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The write result, preserving downstream closure separately from failure and its known cause.</returns>
    /// <remarks>
    /// File writes advance the shared cursor and preserve bytes outside the written range.
    /// WriteAppend selects EOF for every write; other file modes use the current cursor.
    /// Downstream closure tells the producer to stop; it is not a write failure and does not guarantee
    /// delivery of the entire supplied buffer. Callers decide how the result affects command exit status.
    /// The caller keeps content unchanged until completion; the host must copy any bytes retained afterward.
    /// Every failure supplies a diagnostic; unclassified causes use OperationErrorKind.Unknown.
    /// Command exit status and diagnostic policy belong to callers. Cancellation and underlying I/O exceptions may propagate.
    /// </remarks>
    ValueTask<DescriptorWriteResult> WriteAsync(
        FileDescriptor fileDescriptor,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken);

    /// <summary>
    /// Opens a file for truncation, append, or read/write access.
    /// </summary>
    /// <param name="path">The preserved absolute traversal and final-link policy.</param>
    /// <param name="mode">WriteTruncate, WriteAppend, or ReadWrite; read-only access uses a resolved snapshot.</param>
    /// <param name="creationMode">Whether opening requires an existing file, a new file, or accepts either.</param>
    /// <param name="retention">Whether completed output may remain in host file buffers, independently of access and creation.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>Opened or Failed with an operation error. ReadSize is null.</returns>
    /// <remarks>
    /// Resolve components in traversal order before creating or truncating a file. Intermediate links
    /// and trailing slashes obey FilePathLookup semantics. Reject invalid traversal without changing files.
    /// OpenOrCreate accepts an existing file or creates a missing file. OpenExisting requires an existing
    /// file and fails with NotFound without creating a missing one. Both obey the requested access mode;
    /// NoFollow rejects a final symbolic link, including a dangling one. CreateNew atomically claims an absent
    /// name and starts with empty content. Any occupied destination, including a final symbolic link under
    /// either link policy, fails with AlreadyExists without changing it or returning a descriptor.
    /// Enforce creation policy within opening; rejection leaves the destination unchanged and returns no descriptor.
    /// Validate traversal as part of opening, without a separate caller-side metadata check.
    /// ReadWrite preserves existing content and starts one read/write cursor at zero. Reads advance
    /// it only by consumed bytes, never read-ahead. Independent opens have independent cursors;
    /// aliases of one handle share its cursor. Creation policy is independent of these access modes.
    /// Every expected failure supplies a diagnostic and an established OperationError.Kind, or Unknown.
    /// Supply diagnostic detail without command or operand prefixes; callers format the original path.
    /// Retain provider details, including whitespace; do not infer the cause from diagnostic text.
    /// A failed open alone does not establish path absence. The caller closes every successful descriptor.
    /// Allocations use the host's shared MemoryBudget; refusal throws MemoryBudgetExceededException.
    /// Close after failed writes to publish accepted content and release temporary storage.
    /// ReleaseOnClose permits bounded buffering while writing but publishes new output to backing storage
    /// on close, including small outputs. Existing input content and storage-owned buffers are outside
    /// this policy. Writes retain their access and ordering semantics; this is not an atomic replacement.
    /// Reject unsupported retention, access or creation modes before any side effects.
    /// Storage I/O and cancellation retain their usual behavior.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">A retention, access or creation mode is unsupported.</exception>
    Task<FileOpenResult> OpenWritableFileAsync(
        FilePathLookup path, WritableFileOpenMode mode, FileCreationMode creationMode, FileWriteRetention retention,
        CancellationToken cancellationToken);

    /// <summary>Opens a snapshot resolved by this host as an owned, read-only descriptor.</summary>
    /// <remarks>
    /// Independent opens must overlap without holding a filesystem lock during content retrieval.
    /// Expected access failures use FileOpenResult.Failed with a diagnostic and an established
    /// OperationError.Kind, or Unknown. Cancellation and unexpected failures propagate.
    /// Supply diagnostic detail without command or operand prefixes; callers format the original path.
    /// Preserve provider text, including whitespace. An open failure does not establish path absence.
    /// ReadSize describes the selected version and remains null when unknown without reading content.
    /// The caller closes every successful descriptor through CloseDescriptorAsync.
    /// Each open has an independent cursor. If the selected version is unavailable, fail without resolving the path again.
    /// </remarks>
    /// <param name="file">A snapshot captured by this host through path resolution or directory listing.</param>
    /// <param name="mode">Whether retrieval may be deferred or must start before the open completes.</param>
    /// <param name="cancellationToken">Cancellation for acquiring the descriptor.</param>
    Task<FileOpenResult> OpenReadAsync(
        FileSnapshot file, FileReadOpenMode mode, CancellationToken cancellationToken);

    /// <summary>
    /// Releases a host handle and awaits its cleanup.
    /// </summary>
    /// <remarks>
    /// The evaluator owns logical aliases; this method closes their underlying host handle.
    /// Calls arriving while cleanup is in progress await that cleanup and observe the same result or failure.
    /// Once cleanup has finished, closing the retired handle succeeds, including after a failed close.
    /// Calling close transfers cleanup ownership to the host, even after command cancellation.
    /// Cleanup is non-cancellable; failure never restores the handle or requires a retry.
    /// Expected failures preserve provider detail and established OperationError.Kind values, otherwise Unknown.
    /// The returned task represents complete cleanup and publication. Descriptor state must no longer
    /// be mutated by cleanup after completion, including when joining an in-progress close.
    /// </remarks>
    /// <param name="descriptor">The host handle to close.</param>
    /// <returns>Null on success, or the expected cleanup/publication failure without command or operand prefixes.
    /// Already-retired handles complete successfully. Unexpected cleanup exceptions, including cancellation
    /// originating in the provider, propagate after cleanup has terminated.</returns>
    Task<OperationError?> CloseDescriptorAsync(FileDescriptor descriptor);

    /// <summary>
    /// Lists directories, returning one complete listing or expected failure per requested path in order.
    /// </summary>
    /// <param name="paths">Directory lookup paths and final-link policies; duplicates retain their positions.</param>
    /// <param name="metadata">Whether to include already available child metadata, independently of read-ahead.</param>
    /// <param name="snapshots">Whether to capture available regular-file versions, independently of read-ahead.</param>
    /// <param name="accessPattern">Advisory metadata access pattern; hosts may ignore it.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>One result per path, or an empty list for an empty request.</returns>
    /// <remarks>
    /// Resolve preserved components in traversal order. NoFollow rejects a final symbolic link with
    /// NotDirectory; a trailing slash requires a directory and follows the final link under either policy.
    /// Missing and non-directory paths are failures, not empty listings. Describe the failure from
    /// the listing operation itself; an earlier path lookup does not establish listing success.
    /// Returned entries remain immutable and describe the entries themselves, without following child links.
    /// Include dangling links as SymbolicLink. The lookup's final-link policy applies only to the directory
    /// being listed; callers resolve child targets explicitly when needed.
    /// A snapshot request of None returns no snapshots. CaptureAvailable may include regular-file
    /// snapshots already available during listing; do not load lazy children, retrieve file bytes or
    /// open descriptors just to provide one.
    /// A metadata request of None supplies no metadata. Available may include metadata for entries
    /// without following child links, loading lazy children or reading content. Unknown sizes remain
    /// null. Missing metadata is not a listing failure; callers request any missing information
    /// separately. Metadata uses separate optional storage in the result, aligned with the entries,
    /// and describes the same selected entries as their kinds.
    /// When a file has both metadata and a snapshot, they describe the same selected version.
    /// Callers own diagnostic and exit-status policy.
    /// Expected failures affect only their corresponding directories and publish no partial entries.
    /// Hosts may batch metadata retrieval; the results need not represent one atomic filesystem view.
    /// Both access patterns return immediate children only and must not retrieve file bytes.
    /// RecursiveTraversal permits metadata read-ahead; callers still control traversal, pruning and stopping.
    /// Expected read-ahead failures must not fail an otherwise readable directory: fall back to ordinary listing.
    /// Reject unsupported metadata requests, snapshot requests and access patterns before loading paths, even for an empty request.
    /// Use OperationError.Kind for established failure causes; a provider failure alone does not
    /// establish that the requested directory is absent.
    /// Cancellation and unexpected provider exceptions propagate.
    /// </remarks>
    Task<IReadOnlyList<DirectoryListingResult>> ListDirectoriesAsync(
        IReadOnlyList<FilePathLookup> paths, DirectoryMetadataRequest metadata, DirectorySnapshotRequest snapshots,
        DirectoryAccessPattern accessPattern, CancellationToken cancellationToken);

    /// <summary>Borrows a bounded byte window from a descriptor without consuming it.</summary>
    /// <remarks>
    /// Waits only for the request's minimum, or EOF/failure. Increasing the minimum reads ahead while
    /// preserving unconsumed bytes, including when a refill throws. A successful buffer is non-empty;
    /// EOF and failures retain their distinct outcomes. Mark a consumed prefix and dispose the borrow
    /// before awaiting another operation on this descriptor. Unconsumed bytes remain available through aliases.
    /// Content is valid until disposal; use Retain to keep an immutable prefix. Disposal commits without cancellation.
    /// Hosts serialize borrows and cleanup per descriptor while allowing other descriptors to progress.
    /// Failures carry an OperationError and may include usable bytes; callers choose exit status and diagnostics.
    /// Cancellation and underlying I/O exceptions propagate; no borrow transfers on a thrown failure.
    /// </remarks>
    ValueTask<DescriptorReadBuffer> ReadAsync(
        FileDescriptor descriptor, DescriptorReadRequest request, CancellationToken cancellationToken);

    /// <summary>Reports whether each borrowed host descriptor refers to a terminal.</summary>
    /// <param name="descriptors">Resolved host handles to inspect; duplicates retain their positions.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>One boolean per descriptor in input order, or an empty list for an empty request.</returns>
    /// <remarks>
    /// Closed, unknown and non-terminal descriptors return false. Do not consume data or close handles.
    /// Queries must progress independently of filesystem operations, without acquiring filesystem locks.
    /// Cancellation and unexpected host failures propagate.
    /// </remarks>
    Task<IReadOnlyList<bool>> GetTerminalStatusesAsync(
        IReadOnlyList<FileDescriptor> descriptors, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a bounded byte pipe with backpressure and ordered delivery.
    /// </summary>
    /// <param name="lifetimeToken">Aborts the pipe and wakes blocked I/O when cancelled, independently of per-operation tokens.</param>
    /// <param name="cancellationToken">Cancels acquisition only; has no effect on the pipe after successful creation.</param>
    /// <returns>Both owned endpoints, or an error for an expected failure such as descriptor exhaustion.</returns>
    /// <remarks>
    /// Successful writes preserve all bytes; a full pipe waits for consumption rather than truncating.
    /// Reads may run while the writer is open. Writer closure allows buffered bytes to drain before EOF,
    /// still subject to lifetime cancellation. Reader closure wakes pending writes with downstream closure
    /// and ends lifetime monitoring. Until then, lifetime cancellation aborts both ends with descriptor
    /// failures, even if their I/O uses other tokens. An already-cancelled lifetime token yields an aborted
    /// pipe; it does not cancel acquisition. Callers may use one token for both roles.
    /// The caller owns both endpoints and must close them even after lifetime cancellation. Closing releases
    /// resources without cancellation. Failed acquisition transfers no endpoints. Cancellation and unexpected
    /// host failures propagate as exceptions.
    /// </remarks>
    Task<PipeCreationResult> CreatePipeAsync(CancellationToken lifetimeToken, CancellationToken cancellationToken);

    /// <summary>Resolves paths to requested information and optional stable file snapshots.</summary>
    /// <param name="paths">Absolute lookup paths and final-link policies; duplicates retain their positions.</param>
    /// <param name="request">Kind only, metadata without reading contents, or metadata with an exact size.</param>
    /// <param name="snapshots">Whether to capture a file version independently of the information detail.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <remarks>
    /// Returns one result per path in request order, or an empty list for an empty request.
    /// Resolve each preserved path in traversal order, following intermediate symbolic links before '..'.
    /// Follow inspects the final target; NoFollow inspects the final link, including a dangling link.
    /// A trailing slash requires a directory and follows the final link under either policy.
    /// Metadata, when supplied, describes the selected entry, not an unfollowed link's target.
    /// Validate traversal before reading contents for MetadataWithExactSize. An unfollowed link's size
    /// describes the link itself and must not require opening its target.
    /// Check Error before accessing Info; a missing path is a successful lookup with Kind.Missing.
    /// A non-directory traversal component reports NotDirectory through Error, independently of capture.
    /// KindOnly reports existence and kind without requiring metadata; HasMetadata is false.
    /// Report devices, FIFOs, and sockets by their specific kind; Other is for unclassified existing entries.
    /// Metadata requests supply metadata for existing entries; missing paths have no metadata.
    /// EffectiveAccess reports resolved rights in the host's execution context; hosts retain enforcement.
    /// AccessedAt is null when unknown; do not substitute other timestamps.
    /// StatusChangedAt is the last status/path change time, or null when unknown; do not substitute ModifiedAt.
    /// SpecialModeBits is null when unknown and None when known unset, independent of effective access.
    /// OwnershipMatch is null when unknown and None when known non-matching, independent of access rights.
    /// Identity is null when unknown; equal identities denote the same filesystem object, regardless of content.
    /// Expected access and unavailable-content failures affect only their corresponding paths.
    /// Hosts may combine metadata requests; results need not represent one atomic filesystem view.
    /// KindOnly and Metadata must not read file contents. Metadata returns a null size when unknown.
    /// MetadataWithExactSize may retrieve and cache content, and must return a known size for an
    /// existing entry or report failure. Reject unsupported requests before loading any paths.
    /// Capture supplies File for every result, including non-files and lookup failures; None leaves it null.
    /// File reports a snapshot or capture failure independently of Error and Info. Capture failure must
    /// preserve successful path information; metadata failure must not discard an available snapshot.
    /// Info supplies the entry's kind; snapshot success alone does not classify it as a regular file.
    /// Use the NotFile capture outcome only for rejection by entry kind, not traversal or access failures.
    /// Capture itself fetches no content bytes and allocates no streams or descriptors. Select the version
    /// during the same lookup, before any exact-size retrieval; path edits and deletion never change it.
    /// NoFollow rejects final links for capture, including dangling links; their path information remains available.
    /// Hosts may batch traversal and metadata retrieval. Results need not share an atomic filesystem view.
    /// Cancellation and unexpected provider, storage, and I/O exceptions propagate.
    /// </remarks>
    Task<IReadOnlyList<FilePathResolutionResult>> ResolvePathsAsync(
        IReadOnlyList<FilePathLookup> paths, FilePathInfoRequest request, FileSnapshotRequest snapshots, CancellationToken cancellationToken);

}
