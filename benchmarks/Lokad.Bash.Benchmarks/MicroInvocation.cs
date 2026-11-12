using System.Globalization;
using System.Text;
using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using BashEvaluation = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Benchmarks;

internal sealed record MicroObserved(long Checksum, Blob Sample, int PositionalCount, int IndexedCount, bool ExpansionHealthy);

/// <summary>Prepared state is reused; every measured call restores any positional mutation.</summary>
internal sealed class MicroInvocation : IDisposable
{
    private readonly MicroWorkload _row;
    private readonly MicroFixture _layout;
    private readonly Variables _variables = Variables.CreateEmpty();
    private readonly ShellValue[] _callee;
    private readonly IndexedArray _array = new(false);
    private readonly IndexedArrayIndex _window;
    private readonly BindingName _lookup;
    private readonly Word _word;
    private readonly ArithmeticOperand _arithmetic;
    private readonly CommandInvocation _command = new("synthetic", [], ShellPath.Root, [], null, null, null);
    private readonly MemoryGovernor _memory = new(256 * 1024 * 1024);
    private readonly IMemoryScope _owner;
    private readonly BashEvaluation _evaluation;
    private readonly ScopedEvaluation _scope;
    private GlobMatchResult _match;
    private int _matchLength;

    public MicroInvocation(MicroWorkload row)
    {
        _row = row;
        _layout = row.Layout;
        _owner = _memory.CreateScope();
        // Prepared fixture containers/bytes retain this owner across every kernel call.
        _owner.Reserve(checked(4 * 1024 * 1024L + 256L * (_layout.Scalars + _layout.CallerArguments + _layout.CalleeArguments + _layout.ArrayEntries)));
        var value = ShellValue.FromBytes(row.Input.Bytes);
        _variables.TrySetScalar("value", value);
        for (var index = 0; index < _layout.Scalars; index++)
            _variables.TrySetScalar($"v{row.Seed + index}", value);
        _lookup = $"v{row.Seed + _layout.Scalars - 1}";
        _variables.PositionalParameters = Enumerable.Repeat(value, _layout.CallerArguments).ToArray();
        _callee = Enumerable.Repeat(value, _layout.CalleeArguments).ToArray();
        for (var index = 0; index < _layout.ArrayEntries; index++)
        {
            if (!IndexedArrayIndex.TryCreate(row.Seed + (long)index * _layout.Stride, out var position))
                throw new InvalidDataException("Invalid generated array index.");
            _array.Set(position, value);
        }
        if (!IndexedArrayIndex.TryCreate(row.Seed + (long)Math.Max(0, _layout.ArrayEntries - 1) * _layout.Stride, out _window))
            throw new InvalidDataException("Invalid generated window.");
        var source = ShellSource.FromBytes(row.Source.Bytes);
        _word = row.Kind is MicroKind.Literal or MicroKind.Parameter
            ? Tokenizer.ReadExpansionWord(source, ExpansionWordSyntax.Parameter, ShellQuoteKind.None) : new Word([]);
        _arithmetic = row.Kind == MicroKind.ArithmeticPrepared
            ? new ArithmeticSource(source).DirectOperand ?? throw new InvalidDataException("Arithmetic metadata is missing.")
            : ArithmeticOperand.FromValue(source.ToValue());
        _evaluation = new BashEvaluation(Parser.Parse(""), EvaluationDescriptorSetup.Standard, null, null,
            EvaluationOptions.Default, [], _owner);
        _scope = new ScopedEvaluation(_evaluation, _variables, new BashEvaluation.EvaluationContext(0, null, BashEvaluation.ReturnTargetKind.None));
    }

    public long Run()
    {
        long checksum = 0;
        switch (_row.Kind)
        {
            case MicroKind.Literal:
            case MicroKind.Parameter:
                for (var index = 0; index < _row.Scale; index++)
                {
                    using var command = _evaluation.Memory.BeginCommand(_scope);
                    var words = Completed(WordExpansion.ExpandToWordsAsync(_word, _scope));
                    if (words.Count != 1) throw new InvalidDataException("Micro expansion changed field count.");
                    checksum += words[0].ToValue(_evaluation.Memory).Bytes.Length;
                }
                return checksum;
            case MicroKind.VariableLookup:
                for (var index = 0; index < _row.Scale; index++)
                {
                    if (!_variables.TryGetScalar(_lookup, out var value)) throw new InvalidDataException("Micro binding disappeared.");
                    checksum += value.Bytes.Length;
                }
                return checksum;
            case MicroKind.PositionalFrame:
                for (var index = 0; index < _row.Scale; index++)
                {
                    using var frame = _variables.EnterPositionalParametersScope(_callee, PositionalParametersRestorePolicy.Always);
                    checksum += _variables.PositionalParameters.Count;
                }
                return checksum;
            case MicroKind.Shift:
                using (var frame = _variables.EnterPositionalParametersScope(_callee, PositionalParametersRestorePolicy.Always))
                    for (var index = 0; index < _row.Scale; index++)
                    {
                        checksum += _variables.PositionalParameters.Count;
                        if (!_variables.TryShiftPositionalParameters(1)) throw new InvalidDataException("Micro shift failed.");
                    }
                return checksum;
            case MicroKind.ArithmeticValue:
            case MicroKind.ArithmeticPrepared:
                var pending = ArithmeticEvaluation.EvaluateAsync<int>(_arithmetic, Bind, NoWrite);
                if (!pending.IsCompleted) throw new InvalidOperationException("Synchronous micro arithmetic became pending.");
                var result = pending.GetAwaiter().GetResult();
                if (result.Error != null) throw new InvalidDataException("Micro arithmetic failed: " + result.Error);
                return result.Value;
            case MicroKind.GlobUtf8:
            case MicroKind.GlobBytes:
            case MicroKind.GlobAdverse:
                _match = TextGlobMatcher.MatchUtf8(_row.Pattern.Bytes, default, _row.Input.Bytes, true, CancellationToken.None, out _matchLength);
                return _match == GlobMatchResult.Match ? _matchLength : -1;
            case MicroKind.SparseWindow:
                for (var index = 0; index < _row.Scale; index++)
                    foreach (var value in _array.ValuesFrom(_window, 1)) checksum += value.Bytes.Length;
                return checksum;
            case MicroKind.OperationDispatch:
                for (var index = 0; index < _row.Scale; index++)
                    checksum += Completed(RunCommand.AsTask(CompleteOperation, _command)).ShellExitCode;
                return checksum;
            default: throw new InvalidDataException("Unknown micro kind.");
        }

        static ValueTask<ArithmeticBinding<int>> Bind(ArithmeticReference reference, ArithmeticReferenceUse use) =>
            ValueTask.FromResult<ArithmeticBinding<int>>(new ArithmeticBinding<int>.Bound(0, 7));
        static ValueTask<ArithmeticError?> NoWrite(ArithmeticReference reference, int target, long value) =>
            throw new InvalidOperationException("Read-only micro arithmetic attempted a write.");
        static void CompleteOperation(EvaluationOperation operation) =>
            ((RunCommand)operation).SetResult(new CommandExecutionResult.Completed(1));
    }

    public MicroObserved Observe()
    {
        var checksum = Run();
        byte[] sample;
        if (_row.Kind is MicroKind.Literal or MicroKind.Parameter)
        {
            using var command = _evaluation.Memory.BeginCommand(_scope);
            sample = Completed(WordExpansion.ExpandToWordsAsync(_word, _scope)).Single().ToValue(_evaluation.Memory).Bytes.ToArray();
        }
        else if (_row.Kind is MicroKind.GlobUtf8 or MicroKind.GlobBytes or MicroKind.GlobAdverse)
            sample = Encoding.UTF8.GetBytes($"{_match}:{_matchLength}");
        else if (_row.Kind is MicroKind.ArithmeticValue or MicroKind.ArithmeticPrepared)
            sample = Encoding.UTF8.GetBytes(checksum.ToString(CultureInfo.InvariantCulture));
        else if (_row.Kind == MicroKind.VariableLookup)
        {
            if (!_variables.TryGetScalar(_lookup, out var value)) throw new InvalidDataException("Missing micro binding.");
            sample = value.Bytes.ToArray();
        }
        else if (_row.Kind is MicroKind.PositionalFrame or MicroKind.Shift)
            sample = _variables.PositionalParameters[^1].Bytes.ToArray();
        else if (_row.Kind == MicroKind.SparseWindow) sample = _array.ValuesFrom(_window, 1).Single().Bytes.ToArray();
        else sample = Encoding.UTF8.GetBytes("1");
        return new(checksum, Blob.FromBytes(sample), _variables.PositionalParameters.Count, _array.Count, _scope.CanExpand);
    }
    public static void Verify(MicroWorkload row, MicroObserved observed)
    {
        observed.Sample.Validate();
        var layout = row.Layout;
        if (observed.Checksum != row.ExpectedChecksum || observed.Sample.Sha256 != row.ExpectedSample.Sha256
            || observed.Sample.Length != row.ExpectedSample.Length || observed.PositionalCount != layout.CallerArguments
            || observed.IndexedCount != layout.ArrayEntries || !observed.ExpansionHealthy)
            throw new InvalidDataException(row.Id + ": core result or restored state differs from its expectation.");
    }
    public void Dispose()
    {
        _evaluation.Memory.Finish(false);
        _owner.Dispose();
        if (_memory.ReservedBytes != 0) throw new InvalidOperationException("Micro fixture disposal retained admission.");
    }
    private static T Completed<T>(Task<T> pending)
    {
        if (!pending.IsCompleted) throw new InvalidOperationException("Synchronous micro operation became pending.");
        return pending.GetAwaiter().GetResult();
    }
}
