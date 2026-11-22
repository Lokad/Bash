using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Syntax;
using ShellEvaluation = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class EvaluationStartupTests
{
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
            new EvaluationOptions(0, directory), descriptors, environment, argumentZero, arguments);

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
