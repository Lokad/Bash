using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using ShellEvaluation = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class EvaluationStartupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicInheritedStartupSelectsTheCommandAfterLeadingOptions(bool prepared)
    {
        const string source = """printf '%s:%s:%s:%s\n' "${BASH_EXECUTION_STRING+set}" "$0" "$1" "$BASHOPTS" """;
        var label = ShellValue.FromText("chosen");
        var first = ShellValue.FromText("first");
        var invocation = new CommandInvocation("bash",
            [ShellValue.FromText("+O"), ShellValue.FromText("localvar_inherit"), ShellValue.FromText("-c"),
                ShellValue.FromText(source), label, first],
            ShellPath.Root, [new("BASHOPTS", ShellValue.FromText("unknown_option:localvar_inherit"))], null, null, null);
        var script = prepared ? Parser.Parse(source) : new ParsedScript([new ScriptCommand(ShellSource.FromText(source))]);
        var evaluation = ShellEvaluationStartup.CreateInherited(invocation, script, label, [first]);
        await using var host = new TestHost();

        var result = await new Engine().RunAsync(evaluation, host, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("set:chosen:first:localvar_inherit\n", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Contains("BASHOPTS", evaluation.ExportedVariables);
        Assert.Contains("BASHOPTS", evaluation.ReadonlyVariables);
    }

    [Theory]
    [InlineData(false, null, "1", "")]
    [InlineData(true, null, "1", "")]
    [InlineData(false, " \t+007 \t", "8", "")]
    [InlineData(true, "7\n", "1", "")]
    [InlineData(false, "-7", "0", "")]
    [InlineData(true, "998", "999", "")]
    [InlineData(false, "999", "1", "bash: warning: shell level (1000) too high, resetting to 1\n")]
    [InlineData(true, "2000", "1", "bash: warning: shell level (2001) too high, resetting to 1\n")]
    public async Task InheritedShellLevelIsAdjustedAndExported(
        bool prepared, string? inherited, string expectedLevel, string expectedDiagnostic)
    {
        const string source = "inspect \"$SHLVL\"; :";
        var label = ShellValue.FromText("fixture");
        EnvironmentVariable[] environment = inherited == null ? [] : [new("SHLVL", ShellValue.FromText(inherited))];
        var invocation = new CommandInvocation("bash", [ShellValue.FromText("-c"), ShellValue.FromText(source), label],
            ShellPath.Root, environment, null, null, null);
        var script = prepared ? Parser.Parse(source) : new ParsedScript([new ScriptCommand(ShellSource.FromText(source))]);
        var evaluation = ShellEvaluationStartup.CreateInherited(invocation, script, label, []);
        await using var host = new MockFileSystem();

        var result = await new Engine().RunAsync(evaluation, host, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        var command = Assert.Single(host.CommandInvocations);
        var level = ShellValue.FromText(expectedLevel);
        Assert.Equal(level, Assert.Single(command.Arguments));
        Assert.Equal(level, Assert.Single(command.Environment, entry => entry.Name == "SHLVL").Value);
        Assert.Equal(ShellValue.FromText(expectedDiagnostic).Bytes.ToArray(), host.GetOutputBytes(FileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("", " # comment\n", "7")]
    [InlineData("", " # comment\n\n", "8")]
    [InlineData(":\n: && ", "", "7")]
    [InlineData(":; : && ", "", "8")]
    [InlineData(": <<'BODY'; ", "\ntext\nBODY\n", "7")]
    [InlineData(": <<'BODY'; ", "\ntext\nBODY\n\n", "8")]
    public async Task TerminalCommandStringLaunchUsesTheFinalReaderUnit(string before, string after, string expectedChild)
    {
        const string launch = """bash -c 'printf "%s:%s\n" "$1" "$SHLVL"' fixture "$SHLVL" """;
        var source = before + launch + after;
        var label = ShellValue.FromText("fixture");
        var invocation = new CommandInvocation("bash", [ShellValue.FromText("-c"), ShellValue.FromText(source), label],
            ShellPath.Root, [new("SHLVL", ShellValue.FromText("6"))], null, null, null);
        foreach (var script in new[] { Parser.Parse(source), new ParsedScript([new ScriptCommand(ShellSource.FromText(source))]) })
        {
            var evaluation = ShellEvaluationStartup.CreateInherited(invocation, script, label, []);
            await using var host = new TestHost();

            var result = await new Engine().RunAsync(evaluation, host, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal($"7:{expectedChild}\n", host.StandardOutput.TextContent);
            Assert.Empty(host.StandardError.Content.ToArray());
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Fact]
    public async Task ForksShareTheLaunchLevelAndKeepOrdinaryAssignment()
    {
        const string source = """
            declare -p SHLVL
            SHLVL=003
            (SHLVL+=1; printf '%s:%s\n' "$SHLVL" "$BASH_SUBSHELL")
            printf '%s:%s\n' "$SHLVL" "$BASH_SUBSHELL"
            bash -c 'printf "%s:%s\n" "$SHLVL" "$BASH_SUBSHELL"' fixture
            """;
        await using var host = new TestHost();

        var result = await host.RunAsync(source, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("declare -x SHLVL=\"1\"\n0031:1\n003:0\n4:0\n", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Fact]
    public async Task EmptyChildReaderReportsShellLevelWarningToItsInheritedStderr()
    {
        await using var host = new TestHost();

        var result = await host.RunAsync("SHLVL=999 bash -c '' fixture 2>diagnostic; printf '%s\\n' \"$SHLVL\"",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1\n", host.StandardOutput.TextContent);
        Assert.Equal("bash: warning: shell level (1000) too high, resetting to 1\n", host.ReadFile("/diagnostic"));
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommandStringAndItsFunctionsUseDifferentReaderNames(bool prepared)
    {
        const string source = "f(){ missing_body; }\nf\nmissing_top\n";
        var argumentZero = ShellValue.FromByte(0xff);
        var invocation = new CommandInvocation("bash", [ShellValue.FromText("-c"), ShellValue.FromText(source), argumentZero],
            ShellPath.Root, [], null, null, null);
        var script = prepared ? Parser.Parse(source) : new ParsedScript([new ScriptCommand(ShellSource.FromText(source))]);
        var evaluation = ShellEvaluationStartup.CreateInherited(invocation, script, argumentZero, []);
        var governor = new MemoryGovernor(1024 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };

        try
        {
            var result = await new Engine().RunAsync(evaluation, host, owner, CancellationToken.None);

            Assert.Equal(127, result.ExitCode);
            byte[] diagnostics = [.. "environment: line 1: missing_body: command not found\n"u8,
                0xff, .. ": line 3: missing_top: command not found\n"u8];
            Assert.Equal(diagnostics, host.StandardError.Content.ToArray());
            Assert.Equal(0, host.OpenFileCount);
        }
        finally { evaluation.ReleaseFunctions(); }
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CommandStringBindingUsesOriginalBytesAndInheritedExport(bool prepared, bool exported)
    {
        byte[] source = [.. """inspect "$BASH_EXECUTION_STRING" # """u8, 0xff, (byte)'\r', (byte)'\n', (byte)'\n'];
        var commandString = ShellValue.FromBytes(source);
        IReadOnlyList<EnvironmentVariable> environment = exported
            ? [new("BASH_EXECUTION_STRING", ShellValue.FromText("inherited"))] : [];
        var argumentZero = ShellValue.FromText("fixture");
        var invocation = new CommandInvocation("bash", [ShellValue.FromText("-c"), commandString, argumentZero],
            ShellPath.Root, environment, null, null, null);
        var script = prepared ? Parser.Parse(source) : new ParsedScript([new ScriptCommand(ShellSource.FromBytes(source))]);
        var evaluation = ShellEvaluationStartup.CreateInherited(invocation, script, argumentZero, []);
        var governor = new MemoryGovernor(1024 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new MockFileSystem { MemoryBudget = owner };

        try
        {
            var result = await new Engine().RunAsync(evaluation, host, owner, CancellationToken.None);
            Assert.Equal(0, result.ExitCode);
            var command = Assert.Single(host.CommandInvocations);
            Assert.Equal(commandString, Assert.Single(command.Arguments));
            var forwarded = command.Environment.Where(entry => entry.Name == "BASH_EXECUTION_STRING");
            if (exported)
                Assert.Equal(commandString, Assert.Single(forwarded).Value);
            else
                Assert.Empty(forwarded);
            Assert.Empty(host.GetOutputBytes(FileDescriptor.StdErr));
            Assert.Equal(0, host.OpenFileCount);
        }
        finally { evaluation.ReleaseFunctions(); }
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task CommandStringBindingUsesOrdinaryAssignmentAndScope()
    {
        const string script = """
            bash -c '
            initial=$BASH_EXECUTION_STRING
            test -n "$initial" && printf "initial\n"
            f() { local BASH_EXECUTION_STRING; BASH_EXECUTION_STRING=local; printf "%s\n" "$BASH_EXECUTION_STRING"; }
            f
            test "$BASH_EXECUTION_STRING" = "$initial" && printf "restored\n"
            ( BASH_EXECUTION_STRING=child; printf "%s\n" "$BASH_EXECUTION_STRING" )
            BASH_EXECUTION_STRING=changed
            BASH_EXECUTION_STRING+=-value
            printf "%s\n" "$BASH_EXECUTION_STRING"
            unset BASH_EXECUTION_STRING
            printf "%s\n" "${BASH_EXECUTION_STRING-unset}"
            ' fixture
            """;
        await using var host = new TestHost();

        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("initial\nlocal\nrestored\nchild\nchanged-value\nunset\n", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("bash ./input", "unset")]
    [InlineData("BASH_EXECUTION_STRING=inherited bash ./input", "inherited")]
    public async Task FileReaderKeepsTheEnvironmentCommandString(string launch, string expected)
    {
        await using var host = EndToEndHarness.BuildTree([("/input", "printf '%s\\n' \"${BASH_EXECUTION_STRING-unset}\"\n")]);

        var result = await host.RunAsync(launch, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected + "\n", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task NamedChildRetainsItsDefinitionPathThroughASourcedCaller()
    {
        await using var host = EndToEndHarness.BuildTree([
            ("/definition", "f(){\n missing_body\n}\nsource ./caller\n"),
            ("/caller", "\nf\n")
        ]);

        var result = await host.RunAsync("bash ./definition", CancellationToken.None);

        Assert.Equal(127, result.ExitCode);
        Assert.Equal("./definition: line 2: missing_body: command not found\n", host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task CommandStringSyntaxErrorNamesTheCommandReader()
    {
        await using var host = new TestHost();

        var result = await host.RunAsync("bash -c ')' label", CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("label: -c: line 1: syntax error near unexpected token `)'\nlabel: -c: line 1: `)'\n", host.StandardError.TextContent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IfsStartsAssignedAndUnexported(bool prepared)
    {
        const string script = "declare -p IFS\ninspect \"$IFS\"\n"
            + "unset IFS\nwords='left right'; inspect \"${IFS-unset}\" $words\n";
        await using var host = new MockFileSystem();
        var result = prepared
            ? await new Engine().RunAsync(ShellEvaluationStartup.CreateForNewHost(Parser.Parse(script),
                EvaluationOptions.Default), host, CancellationToken.None)
            : await BashScript.RunAsync(host, script, EvaluationOptions.Default, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("declare -- IFS=$' \\t\\n'\n", host.GetOutput(FileDescriptor.StdOut));
        Assert.Empty(host.GetOutputBytes(FileDescriptor.StdErr));
        Assert.Collection(host.CommandInvocations,
            command => Assert.Equal(ShellValue.FromText(" \t\n"), Assert.Single(command.Arguments)),
            command => Assert.Equal(new[] { "unset", "left", "right" }.Select(ShellValue.FromText), command.Arguments));
        Assert.All(host.CommandInvocations, command => Assert.DoesNotContain(command.Environment, entry => entry.Name == "IFS"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("opaque")]
    public async Task InheritedStartupSelectsCapturedIfsWhenProvided(string input)
    {
        ShellValue? value = input switch
        {
            "missing" => null,
            "empty" => ShellValue.Empty,
            "opaque" => ShellValue.FromByte(0xff),
            _ => throw new ArgumentOutOfRangeException(nameof(input))
        };
        IReadOnlyList<EnvironmentVariable> environment = value is { } assigned ? [new("IFS", assigned)] : [];
        var invocation = new CommandInvocation("bash", [], ShellPath.Root, environment, null, null, null);
        var evaluation = ShellEvaluationStartup.CreateInherited(invocation, Parser.Parse("inspect \"$IFS\""),
            ShellValue.FromText("bash"), []);
        await using var host = new MockFileSystem();

        var result = await new Engine().RunAsync(evaluation, host, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        var command = Assert.Single(host.CommandInvocations);
        Assert.Equal(value ?? ShellValue.FromText(" \t\n"), Assert.Single(command.Arguments));
        Assert.Equal(environment, command.Environment.Where(entry => entry.Name == "IFS").ToArray());
        Assert.Empty(host.GetOutputBytes(FileDescriptor.StdErr));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldPwdStartsExportedAndUnassigned(bool prepared)
    {
        const string script = """
            declare -p OLDPWD
            inspect "${OLDPWD-unset}"
            OLDPWD=temporary inspect "${OLDPWD-unset}"
            declare -p OLDPWD
            OLDPWD=
            inspect "${OLDPWD-unset}"
            OLDPWD=previous
            inspect "${OLDPWD-unset}"
            export -n OLDPWD
            inspect "${OLDPWD-unset}"
            unset OLDPWD
            (declare -p OLDPWD 2>/dev/null; printf 'fork=<%s>\n' "$?")
            """;
        await using var host = new MockFileSystem();
        var result = prepared
            ? await new Engine().RunAsync(ShellEvaluationStartup.CreateForNewHost(Parser.Parse(script),
                EvaluationOptions.Default), host, CancellationToken.None)
            : await BashScript.RunAsync(host, script, EvaluationOptions.Default, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("declare -x OLDPWD\ndeclare -x OLDPWD\nfork=<1>\n", host.GetOutput(FileDescriptor.StdOut));
        Assert.Empty(host.GetOutputBytes(FileDescriptor.StdErr));
        Assert.Collection(host.CommandInvocations,
            command => Verify(command, "unset", null),
            command => Verify(command, "unset", "temporary"),
            command => Verify(command, "", ""),
            command => Verify(command, "previous", "previous"),
            command => Verify(command, "previous", null));
        Assert.Equal(0, host.OpenFileCount);

        static void Verify(CommandInvocation command, string argument, string? exported)
        {
            Assert.Equal(ShellValue.FromText(argument), Assert.Single(command.Arguments));
            var values = command.Environment.Where(variable => variable.Name == "OLDPWD");
            if (exported is null)
                Assert.Empty(values);
            else
                Assert.Equal(ShellValue.FromText(exported), Assert.Single(values).Value);
        }
    }

    [Fact]
    public async Task FirstDirectoryChangeExportsThePreviousPath()
    {
        var work = new ShellPath("/work");
        var destination = new ShellPath("/leaf");
        var evaluation = ShellEvaluationStartup.CreateForNewHost(
            Parser.Parse("cd /leaf; inspect \"$OLDPWD\""), new EvaluationOptions(0, work));
        var lookups = new List<ShellPath>();
        await using var host = new MockFileSystem { BeforeGetPathInfo = lookups.Add };
        host.AddDirectory(destination.Path);

        var result = await new Engine().RunAsync(evaluation, host, CancellationToken.None);

        Assert.Equal(new RunResult(0, destination), result);
        Assert.Equal(destination, Assert.Single(lookups));
        var command = Assert.Single(host.CommandInvocations);
        Assert.Equal(ShellValue.FromText(work.Path), Assert.Single(command.Arguments));
        Assert.Equal(ShellValue.FromText(work.Path), Assert.Single(command.Environment, variable => variable.Name == "OLDPWD").Value);
        Assert.Empty(host.GetOutputBytes(FileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("opaque")]
    public async Task InheritedStartupPreservesCapturedOldPwd(string input)
    {
        ShellValue? value = input switch
        {
            "missing" => null,
            "empty" => ShellValue.Empty,
            "opaque" => ShellValue.FromBytes(new byte[] { 0xff, 0x20 }),
            _ => throw new ArgumentOutOfRangeException(nameof(input))
        };
        IReadOnlyList<EnvironmentVariable> environment = value is { } assigned ? [new("OLDPWD", assigned)] : [];
        var parent = new CommandInvocation("bash", [], ShellPath.Root, environment, null, null, null);
        var evaluation = ShellEvaluationStartup.CreateInherited(parent, Parser.Parse("inspect \"${OLDPWD-unset}\""),
            ShellValue.FromText("bash"), []);
        var lookups = new List<ShellPath>();
        await using var host = new MockFileSystem { BeforeGetPathInfo = lookups.Add };

        var result = await new Engine().RunAsync(evaluation, host, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(lookups);
        var command = Assert.Single(host.CommandInvocations);
        Assert.Equal(value ?? ShellValue.FromText("unset"), Assert.Single(command.Arguments));
        Assert.Equal(environment, command.Environment.Where(variable => variable.Name == "OLDPWD").ToArray());
        Assert.Empty(host.GetOutputBytes(FileDescriptor.StdErr));
    }

    [Fact]
    public async Task InheritedStartupImportsValidDefinitionsBesideHostOverrides()
    {
        var invocation = new CommandInvocation("bash", [], ShellPath.Root, [], null, null, null)
        {
            Functions = [new("f(){ printf inherited; }"u8.ToArray()), new("g(){ printf stable; }"u8.ToArray())]
        };
        var evaluation = ShellEvaluationStartup.CreateInherited(invocation, Parser.Parse("f; g"),
            ShellValue.FromText("bash"), []);
        evaluation.SetExportedFunction(Assert.IsType<FunctionDefinition>(
            Assert.Single(Parser.Parse("f(){ printf host; }").Commands)));
        await using var host = new MockFileSystem();
        try
        {
            var result = await new Engine().RunAsync(evaluation, host, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("hoststable"u8.ToArray(), host.GetOutputBytes(FileDescriptor.StdOut));
            Assert.Empty(host.GetOutputBytes(FileDescriptor.StdErr));
        }
        finally { evaluation.ReleaseFunctions(); }
    }

    [Fact]
    public async Task InvalidInheritedSourceLeavesTheChildScriptAndStagedDefinitionsUnpublished()
    {
        var invocation = new CommandInvocation("bash", [], ShellPath.Root, [], null, null, null)
        {
            Functions = [new("f(){ printf inherited; }"u8.ToArray()), new("printf input"u8.ToArray())]
        };
        var evaluation = ShellEvaluationStartup.CreateInherited(invocation, Parser.Parse("printf script"),
            ShellValue.FromText("bash"), []);
        await using var host = new MockFileSystem();
        var result = await new Engine().RunAsync(evaluation, host, CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(host.GetOutputBytes(FileDescriptor.StdOut));
        Assert.Equal("syntax error: Expected a single exported function definition.\n"u8.ToArray(),
            host.GetOutputBytes(FileDescriptor.StdErr));
        Assert.Equal(0, evaluation.RetainedFunctionMemoryBytes);
    }

    [Fact]
    public async Task DirectStartupUsesItsOwnStateAndStandardDescriptors()
    {
        var directory = new ShellPath("/work");
        var evaluation = ShellEvaluationStartup.CreateForNewHost(
            Parser.Parse("inspect \"$#\" \"$PWD\" \"${PARENT_VALUE-unset}\""), new EvaluationOptions(0, directory));
        var host = new MockFileSystem();

        var result = await new Engine().RunAsync(evaluation, host, CancellationToken.None);

        Assert.Equal(new RunResult(0, directory), result);
        var command = Assert.Single(host.CommandInvocations);
        Assert.Equal(new[] { ShellValue.FromText("0"), ShellValue.FromText("/work"), ShellValue.FromText("unset") },
            command.Arguments);
        Assert.Equal(directory, command.CurrentDirectory);
        Assert.Equal(FileDescriptor.StdIn, command.StdIn);
        Assert.Equal(FileDescriptor.StdOut, command.StdOut);
        Assert.Equal(FileDescriptor.StdErr, command.StdErr);
        Assert.Empty(evaluation.GetDescriptorsForCleanup());
    }

    [Fact]
    public async Task InitializationCopiesByteArgumentsAndEnvironmentWithoutCommandInvocation()
    {
        var directory = new ShellPath("/work");
        var argumentZero = ShellValue.FromBytes(new byte[] { 0xfe });
        var positional = ShellValue.FromBytes(new byte[] { 0xff });
        var value = ShellValue.FromBytes(new byte[] { 0xa9 });
        var arguments = new List<ShellValue> { positional };
        var environment = new List<EnvironmentVariable>
        {
            new("PWD", ShellValue.FromText("/different")),
            new("VALUE", value)
        };
        var output = new FileDescriptor(11);
        var descriptors = new EvaluationDescriptorSetup(
            DescriptorTable.Snapshot.Standard.WithStandardStreams(FileDescriptor.StdIn, output, FileDescriptor.StdErr),
            [output]);
        var evaluation = new ShellEvaluation(Parser.Parse("inspect \"$0\" \"$1\" \"$#\" \"$PWD\" \"$VALUE\""),
            new EvaluationOptions(0, directory), descriptors, "bash", environment, argumentZero, arguments, ShellOption.None);

        arguments.Clear();
        environment.Clear();
        var host = new MockFileSystem();
        await new Engine().RunAsync(evaluation, host, CancellationToken.None);

        Assert.Equal(0, evaluation.LastExitCode);
        var command = Assert.Single(host.CommandInvocations);
        Assert.Equal(new[] { argumentZero, positional, ShellValue.FromText("1"), ShellValue.FromText("/different"), value },
            command.Arguments);
        Assert.Equal(directory, command.CurrentDirectory);
        Assert.Equal(output, command.StdOut);
        Assert.Empty(command.DescriptorsToClose);
        Assert.Empty(evaluation.GetDescriptorsForCleanup());
    }
}
