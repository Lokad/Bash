using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lokad.Bash.Commands;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Evaluation;

/// <summary>
/// Expands glob patterns using directory listings.
/// </summary>
/// <remarks>
/// Fixed prefixes select the initial directory. After wildcard expansion, every
/// ordinary suffix component must exist; unmatched patterns stay literal.
/// </remarks>
internal sealed class GlobEvaluation
{
    /// <summary> Stores the original pattern used for fallback results. </summary>
    private readonly string _pattern;
    /// <summary> Owns the pathname segments prepared for this expansion. </summary>
    private readonly IReadOnlyList<GlobPattern.PatternSegment> _segments;
    private readonly EvaluationMemory _memory;
    private readonly CancellationToken _cancellationToken;
    /// <summary> Holds the active candidate list for the current segment; rebuilt as segments advance. </summary>
    private readonly List<Candidate> _candidates = [];
    /// <summary> Holds the queue of directories to list for the current glob segment; null until listing begins. </summary>
    private Queue<Candidate>? _pendingDirectories;
    /// <summary> Collects candidates for the next segment while listing directories. </summary>
    private List<Candidate>? _nextCandidates;
    /// <summary> Tracks the current segment index as expansion proceeds. </summary>
    private int _segmentIndex;
    // Leading literal paths need no listing; suffixes after a wildcard must actually exist.
    private bool _hasExpandedWildcard;
    /// <summary> Stores the segment currently being matched against directory entries. </summary>
    private string _currentSegment = string.Empty;
    /// <summary> Stores the mask for the current segment. </summary>
    private GlobMask _currentMask = new(ReadOnlyMemory<byte>.Empty);
    /// <summary> Indicates whether the current segment is the final segment in the pattern. </summary>
    private bool _currentSegmentIsFinal;
    /// <summary> Indicates that a directory listing is in progress for the current candidate. </summary>
    private bool _listingActive;
    /// <summary> Stores the candidate whose directory listing is being applied. </summary>
    private Candidate _listingCandidate;
    /// <summary> Stores the final expanded results once the evaluation completes. </summary>
    private IReadOnlyList<string>? _results;
    /// <summary> Tracks whether the expansion has completed and results are stable. </summary>
    private bool _completed;
    /// <summary> Stores the absolute root path used when patterns start with '/'. </summary>
    private const string RootPath = "/";

    /// <summary>
    /// Initializes a new instance of the <see cref="GlobEvaluation"/> class.
    /// </summary>
    /// <param name="pattern">The glob pattern with quote metadata.</param>
    /// <param name="directory">The directory path.</param>
    /// <param name="memory">Shared memory for pathname components, intermediate candidates and copied path characters.</param>
    /// <param name="cancellationToken">Cancels directory traversal and candidate construction.</param>
    public GlobEvaluation(
        GlobPattern pattern,
        string directory,
        EvaluationMemory memory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        memory.Charge(256);
        _pattern = pattern.Pattern;
        _segments = pattern.SplitPathSegments(memory);
        _memory = memory;
        _cancellationToken = cancellationToken;

        var listBase = pattern.IsAbsolute ? RootPath : (string.IsNullOrEmpty(directory) ? "." : directory);
        var outputBase = pattern.IsAbsolute ? RootPath : string.Empty;
        _candidates.Add(new Candidate(listBase, outputBase));
    }

    /// <summary>
    /// Executes the glob expansion by requesting directory listings as needed.
    /// </summary>
    /// <returns>The expanded glob matches.</returns>
    public async Task<IReadOnlyList<string>> EvaluateAsync(IFileTester tester)
    {
        while (!_completed)
        {
            Advance();

            if (_completed || _pendingDirectories == null || _pendingDirectories.Count == 0)
                break;

            var candidate = _pendingDirectories.Dequeue();
            _listingCandidate = candidate;
            _listingActive = true;
            var entries = await tester.ListDirectoryAsync(candidate.ListPath).ConfigureAwait(false);
            await ApplyDirectoryEntriesAsync(entries, tester).ConfigureAwait(false);
        }

        return _results ?? [_pattern];
    }

    /// <summary>
    /// Advances the glob state machine to the next directory listing or completion.
    /// </summary>
    private void Advance()
    {
        while (!_completed)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (_pendingDirectories != null)
            {
                if (_pendingDirectories.Count > 0)
                    return;

                if (_nextCandidates == null || _nextCandidates.Count == 0)
                {
                    _results = [_pattern];
                    _completed = true;
                    _pendingDirectories = null;
                    _nextCandidates = null;
                    return;
                }

                _candidates.Clear();
                _candidates.AddRange(_nextCandidates);
                _pendingDirectories = null;
                _nextCandidates = null;
                _segmentIndex++;
                continue;
            }

            if (_segmentIndex >= _segments.Count)
            {
                _results = BuildResults();
                _completed = true;
                return;
            }

            var segment = _segments[_segmentIndex];
            var mask = segment.MetaMask.AsSpan();
            // Listings omit dot entries; existing directory candidates can traverse them directly.
            if (GlobPattern.RequiresExpansion(segment.Pattern.AsSpan(), in mask)
                || _hasExpandedWildcard && segment.Pattern is not "." and not "..")
            {
                BeginGlobSegment(segment);
                if (_pendingDirectories == null || _pendingDirectories.Count == 0)
                {
                    _results = [_pattern];
                    _completed = true;
                }

                return;
            }

            ApplyFixedSegment(segment.Pattern);
            _segmentIndex++;
        }

        IReadOnlyList<string> BuildResults() =>
            _candidates.Count == 0
                ? [_pattern]
                : _candidates
                    // Bash sorts pathname expansion results instead of preserving filesystem enumeration order.
                    .OrderBy(static candidate => candidate.OutputPath, StringComparer.Ordinal)
                    .Select(static candidate => candidate.OutputPath)
                    .ToArray();
    }

    /// <summary>
    /// Applies a directory listing to the current glob segment and accumulates next candidates.
    /// </summary>
    private async Task ApplyDirectoryEntriesAsync(IReadOnlyList<DirectoryEntry> entries, IFileTester tester)
    {
        if (_pendingDirectories == null || !_listingActive)
            throw new InvalidOperationException("Directory listings are not pending for this glob expansion.");

        _listingActive = false;
        if (_nextCandidates == null)
            throw new InvalidOperationException("Next candidates were not initialized.");

        var matchDot = _currentSegment.Length > 0 && _currentSegment[0] == '.';

        foreach (var entry in entries)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var nameSpan = entry.Name.AsSpan();
            if (nameSpan.Length == 0)
                continue;

            if (!matchDot && nameSpan[0] == '.')
                continue;

            if (TextGlobMatcher.MatchText(_currentSegment.AsSpan(), _currentMask.AsSpan(), nameSpan,
                    _cancellationToken) == GlobMatchResult.Match)
            {
                if (!_currentSegmentIsFinal && entry.Kind is not (FilePathKind.Directory or FilePathKind.SymbolicLink))
                    continue;

                var candidate = AppendSegment(_listingCandidate, entry.Name);
                // A final match includes dangling links; only further traversal needs the target kind.
                if (!_currentSegmentIsFinal && entry.Kind == FilePathKind.SymbolicLink
                    && !await tester.IsDirectoryAsync(candidate.ListPath).ConfigureAwait(false))
                    continue;
                _nextCandidates.Add(candidate);
            }
        }
    }

    /// <summary>
    /// Initializes state for a glob segment that requires directory listings.
    /// </summary>
    private void BeginGlobSegment(GlobPattern.PatternSegment segment)
    {
        _hasExpandedWildcard = true;
        _currentSegment = segment.Pattern;
        _currentMask = segment.MetaMask;
        _currentSegmentIsFinal = _segmentIndex == _segments.Count - 1;
        _pendingDirectories = new Queue<Candidate>(_candidates);
        _nextCandidates = [];
    }

    /// <summary>
    /// Appends a fixed, non-glob segment to all current candidates.
    /// </summary>
    private void ApplyFixedSegment(string segment)
    {
        for (var index = 0; index < _candidates.Count; index++)
        {
            _candidates[index] = AppendSegment(_candidates[index], segment);
        }
    }

    /// <summary>Charges intermediate candidates even when a later segment leaves no matches.</summary>
    private Candidate AppendSegment(Candidate candidate, string segment)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        _memory.Charge(256);
        return new Candidate(
            CombinePath(candidate.ListPath, segment, collapseDot: true),
            CombinePath(candidate.OutputPath, segment, collapseDot: false));

        string CombinePath(string prefix, string suffix, bool collapseDot)
        {
            if (string.IsNullOrEmpty(suffix))
                return prefix;

            if (string.IsNullOrEmpty(prefix))
                return suffix;

            if (collapseDot && prefix is ['.'])
                return suffix;

            if (prefix is ['/'])
            {
                _memory.Charge(checked(4L * (1L + suffix.Length)));
                return "/" + suffix;
            }

            _memory.Charge(checked(4L * ((long)prefix.Length + 1 + suffix.Length)));
            return prefix + "/" + suffix;
        }
    }

    /// <summary> Represents a pair of list and output paths for glob expansion. </summary>
    private readonly record struct Candidate(string ListPath, string OutputPath);
}
