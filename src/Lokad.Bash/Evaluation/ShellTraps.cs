using System;
using System.Collections.Generic;
using System.Globalization;

namespace Lokad.Bash.Evaluation;

internal enum TrapCondition { Exit = 0, Hangup = 1, Interrupt = 2, Terminate = 15 }

/// <summary>Per-shell registrations; caught handlers are inactive in children until explicitly installed there.</summary>
internal sealed class ShellTraps
{
    public static IReadOnlyList<TrapCondition> Conditions { get; } =
        [TrapCondition.Exit, TrapCondition.Hangup, TrapCondition.Interrupt, TrapCondition.Terminate];

    private readonly Dictionary<TrapCondition, ShellValue> _handlers = [];
    private IReadOnlyDictionary<TrapCondition, ShellValue>? _inheritedForListing;
    private ShellValue _runningAction;
    private bool _exitHandled;
    private const int EntryMemoryBytes = 128;

    public int? ExitTrapStatus { get; private set; }

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
    public void DiscardInheritedListing() => _inheritedForListing = null;

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

    public ShellTraps Fork(ShellForkKind kind)
    {
        // Bash keeps an active EXIT context in substitutions and simple pipeline commands.
        // Explicit subshells and compound pipeline stages start with no handler in progress.
        var inheritExitContext = kind != ShellForkKind.Subshell && ExitTrapStatus.HasValue;
        var child = new ShellTraps
        {
            _inheritedForListing = new Dictionary<TrapCondition, ShellValue>(ListedHandlers),
            _exitHandled = inheritExitContext,
            ExitTrapStatus = inheritExitContext ? ExitTrapStatus : null
        };
        foreach (var (condition, action) in _handlers)
            if (action.Bytes.IsEmpty)
                child._handlers.Add(condition, action);
        return child;
    }

    public bool TryBeginExit(int status, out ShellValue action)
    {
        action = ShellValue.Empty;
        if (_exitHandled)
            return false;
        _exitHandled = true;
        if (!_handlers.TryGetValue(TrapCondition.Exit, out action) || action.Bytes.IsEmpty)
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
        if (!value.TryGetUtf8Text(out var text))
            return false;
        var name = text.AsSpan();
        if (name.Equals("EXIT", StringComparison.OrdinalIgnoreCase))
            return true;
        if (int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number is 0 or 1 or 2 or 15)
        {
            condition = (TrapCondition)number;
            return true;
        }
        if (name.StartsWith("SIG", StringComparison.OrdinalIgnoreCase))
            name = name[3..];
        if (name.Equals("HUP", StringComparison.OrdinalIgnoreCase))
            condition = TrapCondition.Hangup;
        else if (name.Equals("INT", StringComparison.OrdinalIgnoreCase))
            condition = TrapCondition.Interrupt;
        else if (name.Equals("TERM", StringComparison.OrdinalIgnoreCase))
            condition = TrapCondition.Terminate;
        else
            return false;
        return true;
    }

    public static string GetName(TrapCondition condition) => condition switch
    {
        TrapCondition.Exit => "EXIT",
        TrapCondition.Hangup => "SIGHUP",
        TrapCondition.Interrupt => "SIGINT",
        TrapCondition.Terminate => "SIGTERM",
        _ => throw new ArgumentOutOfRangeException(nameof(condition))
    };
}
