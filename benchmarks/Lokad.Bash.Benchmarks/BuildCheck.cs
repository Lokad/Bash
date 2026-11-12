using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Characteristics;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.Results;

namespace Lokad.Bash.Benchmarks;

/// <summary>Generates and compiles each wrapper shape without an executor or timing runner.</summary>
internal static class BuildCheck
{
    public static void Run(string[] args)
    {
        if (args.Length != 2 || Environment.Version.Major is not (10 or 11))
            throw new ArgumentException("build-check DIRECTORY (on .NET 10 or .NET 11)");
        var directory = Path.GetFullPath(args[1]);
        var root = Path.Combine(directory, "builds", $"net{Environment.Version.Major}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var catalog = WorkloadCatalog.Read(Path.Combine(directory, "catalog.json"));
        var macro = catalog.Cases.First(row => row.SkipReason.Length == 0 && row.Domain == WorkloadDomain.Language);
        var micro = MicroCatalog.Read(Path.Combine(directory, "micro-catalog.json")).Cases[0];
        var source = MeasurementDispatch.IdentifySource();
        var oldMacro = Environment.GetEnvironmentVariable(MeasurementContext.EnvironmentKey);
        var oldMicro = Environment.GetEnvironmentVariable(MicroContext.EnvironmentKey);
        var runs = new List<BenchmarkRunInfo>();
        var infos = new List<BenchmarkBuildInfo>();
        GenerateResult? generated = null;
        BuildResult? built = null;
        IReadOnlyList<BenchmarkAsset> assets = [];
        var complete = false;
        var setupGuardsChecked = false;
        var error = "";
        Console.WriteLine($"Compiling five benchmark wrapper types on .NET {Environment.Version.Major}; no timing job or executor will start.");
        try
        {
            foreach (var type in new[] { typeof(ParseBenchmarks), typeof(PreparedBenchmarks), typeof(PublicBenchmarks), typeof(ProcessBenchmarks), typeof(CoreBenchmarks) })
            {
                var typeRoot = Path.Combine(root, type.Name);
                Directory.CreateDirectory(typeRoot);
                var path = Path.Combine(typeRoot, "context.json");
                var key = type == typeof(CoreBenchmarks) ? MicroContext.EnvironmentKey : MeasurementContext.EnvironmentKey;
                if (type == typeof(CoreBenchmarks))
                    File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new MicroContext(directory, [micro.Id], null), WorkloadCatalog.Json));
                else new MeasurementContext(directory, root, type == typeof(ParseBenchmarks) ? MeasurementLane.Parse
                    : type == typeof(PreparedBenchmarks) ? MeasurementLane.Prepared : type == typeof(PublicBenchmarks) ? MeasurementLane.Public
                    : MeasurementLane.ManagedProcess, [macro.Id], null, null).Write(path);
                Environment.SetEnvironmentVariable(key, path);
                var config = MeasurementDispatch.Configure(typeRoot, key, path);
                if (!config.Options.HasFlag(ConfigOptions.DisableParallelBuild))
                    throw new InvalidOperationException("Future-runtime build scheduling must remain explicit.");
                var run = BenchmarkConverter.TypeToBenchmarks(type, config);
                runs.Add(run);
                _ = MeasurementDispatch.ValidateDiscovery(run, 1, typeRoot);
                infos.Add(new BenchmarkBuildInfo(run.BenchmarksCases.Single(), run.Config, infos.Count));
            }
            var partition = new BuildPartition(infos.ToArray(), new CompositeResolver(EnvironmentResolver.Instance, InfrastructureResolver.Instance));
            var toolchain = BenchmarkToolchain.Create($"net{Environment.Version.Major}.0");
            var toolchainErrors = infos.SelectMany(info => toolchain.Validate(info.BenchmarkCase, partition.Resolver)).ToArray();
            if (toolchainErrors.Any(item => item.IsCritical))
                throw new InvalidDataException("Toolchain validation failed: " + string.Join("; ", toolchainErrors.Select(item => item.Message)));
            using var writer = new StreamWriter(Path.Combine(root, "build.log"), false, new UTF8Encoding(false));
            var logger = new StreamLogger(writer);
            generated = toolchain.Generator.GenerateProject(partition, logger, root);
            if (!generated.IsGenerateSuccess) throw new InvalidOperationException("Generated benchmark project failed.", generated.GenerateException);
            File.Copy(generated.ArtifactsPaths.ProjectFilePath, Path.Combine(root, "generated.csproj"));
            File.Copy(generated.ArtifactsPaths.ProgramCodePath, Path.Combine(root, "generated.notcs"));
            var assetPath = Path.Combine(generated.ArtifactsPaths.BuildArtifactsDirectoryPath, "qualified-assets.json");
            File.Copy(assetPath, Path.Combine(root, "qualified-assets.json"));
            assets = JsonSerializer.Deserialize<BenchmarkAsset[]>(File.ReadAllBytes(assetPath), WorkloadCatalog.Json)
                ?? throw new InvalidDataException("Missing qualified binary assets.");
            built = toolchain.Builder.Build(generated, partition, logger);
            if (!built.IsBuildSuccess) throw new InvalidOperationException("Generated benchmark build failed: " + built.ErrorMessage);
            foreach (var asset in assets)
                if (Blob.Hash(File.ReadAllBytes(Path.Combine(generated.ArtifactsPaths.BinariesDirectoryPath, asset.FileName))) != asset.Sha256)
                    throw new InvalidDataException("Generated wrapper changed a qualified binary: " + asset.FileName);
            // Metadata discovery is permitted here. Execution still requires the VM declaration.
            foreach (var setup in new Action[] { new ParseBenchmarks().Setup, () => new PreparedBenchmarks().Setup().GetAwaiter().GetResult(),
                () => new PublicBenchmarks().Setup().GetAwaiter().GetResult(), new ProcessBenchmarks().Setup, new CoreBenchmarks().Setup })
            {
                var rejected = false;
                try { setup(); }
                catch (InvalidDataException exception) when (exception.Message.Contains("quiet dedicated VM", StringComparison.Ordinal)) { rejected = true; }
                if (!rejected) throw new InvalidOperationException("A build-only context allowed benchmark setup without the VM declaration.");
            }
            setupGuardsChecked = true;
            complete = true;
        }
        catch (Exception exception) { error = exception.ToString(); throw; }
        finally
        {
            File.WriteAllBytes(Path.Combine(root, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1, complete, timingsCollected = false, executorInvoked = false,
                recordedAt = DateTimeOffset.UtcNow, source, runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                sdk = HostEnvironmentInfo.GetCurrent().DotNetSdkVersion.Value, buildScheduling = "Sequential",
                framework = $"net{Environment.Version.Major}.0", wrapperTypes = infos.Select(info => info.BenchmarkCase.Descriptor.Type.FullName),
                generated = generated?.IsGenerateSuccess == true, built = built?.IsBuildSuccess == true,
                copiedAssetsVerified = complete, setupGuardsChecked, assets, error,
                harnessSha256 = Blob.Hash(File.ReadAllBytes(typeof(BuildCheck).Assembly.Location)),
                interpreterSha256 = Blob.Hash(File.ReadAllBytes(typeof(BashScript).Assembly.Location))
            }, WorkloadCatalog.Json));
            foreach (var run in runs) run.Dispose();
            Environment.SetEnvironmentVariable(MeasurementContext.EnvironmentKey, oldMacro);
            Environment.SetEnvironmentVariable(MicroContext.EnvironmentKey, oldMicro);
        }
        Console.WriteLine($"Compiled five wrapper types; {assets.Count} copied assemblies match their qualified hashes. Evidence: {Path.Combine(root, "manifest.json")}");
    }
}
