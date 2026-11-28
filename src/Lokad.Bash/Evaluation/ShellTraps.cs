using System;
using System.Collections.Generic;
using System.Text;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Evaluation;

internal enum TrapCondition { Exit = 0, Hangup = 1, Interrupt = 2, Terminate = 15 }

/// <summary>Per-shell registrations, inherited listings and EXIT during direct pipeline expansion.</summary>
internal sealed class ShellTraps
{
    public static IReadOnlyList<TrapCondition> Conditions { get; } =
        [TrapCondition.Exit, TrapCondition.Hangup, TrapCondition.Interrupt, TrapCondition.Terminate];

    private readonly Dictionary<TrapCondition, ShellValue> _handlers = [];
    private IReadOnlyDictionary<TrapCondition, ShellValue>? _inheritedForListing;
    private ShellValue _inheritedPipelineExitAction;
    private ShellValue _runningAction;
    private bool _exitHandled;
    private const int EntryMemoryBytes = 128;

    public int? ExitTrapStatus { get; private set; }

    internal bool HasPendingExitAction =>
        !_exitHandled && _handlers.TryGetValue(TrapCondition.Exit, out var action) && !action.IsEmpty;

    internal bool HasInheritedPipelineExitAction => !_exitHandled && !_inheritedPipelineExitAction.IsEmpty;

    internal bool PreventsTerminalReplacement
    {
        get
        {
            if (ExitTrapStatus.HasValue)
                return true;
            foreach (var (condition, action) in _handlers)
                if (condition == TrapCondition.Exit || !action.IsEmpty)
                    return true;
            return false;
        }
    }

    // Retain the EXIT latch and saved status even if the action clears all registrations.
    internal bool CanRetire => !_exitHandled && _handlers.Count == 0 && ListedHandlers.Count == 0;

    public IReadOnlyDictionary<TrapCondition, ShellValue> ListedHandlers => _inheritedForListing ?? _handlers;

    // At most four registrations. Shared actions are conservatively counted per condition.
    public long EstimatedMemoryBytes
    {
        get
        {
            var bytes = Variables.EstimateValueBytes(_runningAction);
            foreach (var action in ListedHandlers.Values)
                bytes = checked(bytes + EntryMemoryBytes + Variables.EstimateValueBytes(action));
            return bytes;
        }
    }

    /// <summary>A registration/reset attempt discards inherited listings, even if all its conditions are invalid.</summary>
    public void DiscardInheritedListing()
    {
        _inheritedForListing = null;
        _inheritedPipelineExitAction = ShellValue.Empty;
    }

    public void Set(TrapCondition condition, ShellValue action, EvaluationMemory memory)
    {
        var previousBytes = _handlers.TryGetValue(condition, out var previous)
            ? EntryMemoryBytes + Variables.EstimateValueBytes(previous) : 0;
        var bytes = EntryMemoryBytes + Variables.EstimateValueBytes(action);
        // Admit retained growth before publication; command scratch already covers the action.
        memory.Charge(Math.Max(0, bytes - previousBytes));
        _handlers[condition] = action;
    }

    public void Reset(TrapCondition condition) => _handlers.Remove(condition);

    // The child owns the listing and the active map for ignored signals. Action bytes stay shared.
    internal long EstimateForkBackingBytes() => 128L + EntryMemoryBytes * (ListedHandlers.Count + _handlers.Count);

    public ShellTraps Fork(ShellForkKind kind)
    {
        // Bash keeps an active EXIT context in substitutions and simple pipeline commands.
        // Explicit subshells and compound pipeline stages start with no handler in progress.
        var inheritExitContext = kind != ShellForkKind.Subshell && ExitTrapStatus.HasValue;
        var listed = ListedHandlers;
        var child = new ShellTraps
        {
            _inheritedForListing = listed.Count == 0 ? null : new Dictionary<TrapCondition, ShellValue>(listed),
            _exitHandled = inheritExitContext,
            ExitTrapStatus = inheritExitContext ? ExitTrapStatus : null
        };
        foreach (var (condition, action) in _handlers)
            if (action.Bytes.IsEmpty)
                child._handlers.Add(condition, action);
            else if (kind == ShellForkKind.PipelineSimpleCommand && condition == TrapCondition.Exit)
                child._inheritedPipelineExitAction = action;
        return child;
    }

    public bool TryBeginExit(int status, bool useInheritedPipelineAction, out ShellValue action)
    {
        action = ShellValue.Empty;
        if (_exitHandled)
            return false;
        _exitHandled = true;
        if (useInheritedPipelineAction)
            action = _inheritedPipelineExitAction;
        else
            _handlers.TryGetValue(TrapCondition.Exit, out action);
        if (action.IsEmpty)
            return false;
        // A handler may reset or replace itself before its parsed commands finish.
        _runningAction = action;
        ExitTrapStatus = status;
        return true;
    }

    public void EndExit()
    {
        _runningAction = ShellValue.Empty;
        ExitTrapStatus = null;
    }

    public static bool TryParse(ShellValue value, out TrapCondition condition)
    {
        condition = default;
        var name = value.Bytes.Span;
        if (Ascii.EqualsIgnoreCase(name, "EXIT"u8))
            return true;
        if (Integers.TryParseBuiltinNumber(name, out var number)
            && number is 0 or 1 or 2 or 15)
        {
            condition = (TrapCondition)number;
            return true;
        }
        if (name.Length >= 3 && Ascii.EqualsIgnoreCase(name[..3], "SIG"u8))
            name = name[3..];
        if (Ascii.EqualsIgnoreCase(name, "HUP"u8))
            condition = TrapCondition.Hangup;
        else if (Ascii.EqualsIgnoreCase(name, "INT"u8))
            condition = TrapCondition.Interrupt;
        else if (Ascii.EqualsIgnoreCase(name, "TERM"u8))
            condition = TrapCondition.Terminate;
        else
            return false;
        return true;
    }

    public static ReadOnlySpan<byte> GetNameBytes(TrapCondition condition) => condition switch
    {
        TrapCondition.Exit => "EXIT"u8,
        TrapCondition.Hangup => "SIGHUP"u8,
        TrapCondition.Interrupt => "SIGINT"u8,
        TrapCondition.Terminate => "SIGTERM"u8,
        _ => throw new ArgumentOutOfRangeException(nameof(condition))
    };
}
