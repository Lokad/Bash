using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Xml.Linq;
using BenchmarkDotNet.Characteristics;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains;
using BenchmarkDotNet.Toolchains.CsProj;
using BenchmarkDotNet.Toolchains.DotNetCli;
using BenchmarkDotNet.Toolchains.Results;
using BenchmarkDotNet.Validators;

namespace Lokad.Bash.Benchmarks;

internal sealed record BenchmarkAsset(string FileName, string Sha256);

/// <summary>Builds the timing wrapper against the exact binaries that supplied correctness evidence.</summary>
internal static class BenchmarkToolchain
{
    public static IToolchain Create(string framework, string runDirectory)
    {
        var standard = CsProjCoreToolchain.From(new NetCoreAppSettings(framework, null, framework));
        return new CompiledReferences(standard, new CompiledGenerator(framework, runDirectory));
    }

    private sealed class CompiledReferences(IToolchain standard, IGenerator generator) : IToolchain
    {
        public string Name => "Qualified binaries " + standard.Name;
        public IGenerator Generator => generator;
        public IBuilder Builder { get; } = new QualifiedBuilder(standard.Builder);
        public IExecutor Executor => standard.Executor;
        public bool IsInProcess => standard.IsInProcess;
        public IEnumerable<ValidationError> Validate(BenchmarkCase benchmarkCase, IResolver resolver)
        {
            var runtime = benchmarkCase.GetRuntime();
            if (runtime.RuntimeMoniker != RuntimeMoniker.NotRecognized)
                return standard.Validate(benchmarkCase, resolver);
            // CreateForNewVersion retains honest .NET 11 identity, but this BDN
            // version's SDK enum lookup throws for it. Validate the actual SDK/TFM.
            var sdk = HostEnvironmentInfo.GetCurrent().DotNetSdkVersion.Value.Split('-')[0];
            if (runtime is not CoreRuntime || runtime.MsBuildMoniker != "net11.0" || Environment.Version.Major != 11
                || !Version.TryParse(sdk, out var version) || version.Major != 11)
                return [new ValidationError(true, "The .NET 11 job requires the pinned SDK 11 and matching driver runtime.", benchmarkCase)];
            return new Toolchain(Name, Generator, Builder, Executor).Validate(benchmarkCase, resolver);
        }
    }

    private sealed class QualifiedBuilder(IBuilder builder) : IBuilder
    {
        public BuildResult Build(GenerateResult generated, BuildPartition partition, ILogger logger)
        {
            var built = builder.Build(generated, partition, logger);
            if (!built.IsBuildSuccess) return built;
            var paths = generated.ArtifactsPaths;
            var assets = ReadAssets(paths.BuildArtifactsDirectoryPath);
            foreach (var asset in assets)
            {
                var frozen = Path.Combine(paths.BuildArtifactsDirectoryPath, "qualified-binaries", asset.FileName);
                var output = Path.Combine(paths.BinariesDirectoryPath, asset.FileName);
                if (Blob.Hash(File.ReadAllBytes(frozen)) != asset.Sha256 || Blob.Hash(File.ReadAllBytes(output)) != asset.Sha256)
                    throw new InvalidDataException("Generated wrapper changed a qualified binary: " + asset.FileName);
            }
            return built;
        }
    }

    internal static IReadOnlyList<BenchmarkAsset> ReadAssets(string directory) =>
        JsonSerializer.Deserialize<BenchmarkAsset[]>(File.ReadAllBytes(Path.Combine(directory, "qualified-assets.json")), WorkloadCatalog.Json)
        ?? throw new InvalidDataException("Missing qualified binary assets.");

    private sealed class CompiledGenerator(string framework, string runDirectory) : CsProjGenerator(framework, "", "", "", true)
    {
        protected override string GetBuildArtifactsDirectoryPath(BuildPartition partition, string programName)
        {
            var directory = Path.Combine(runDirectory, "wrapper-builds", programName);
            EvidenceFiles.CreateDirectory(directory);
            File.Copy(Path.Combine(VerificationGate.FindCheckout(), "global.json"), Path.Combine(directory, "global.json"));
            return directory;
        }

        protected override FileInfo GetProjectFilePath(Type benchmarkTarget, ILogger logger)
        {
            var name = benchmarkTarget.Assembly.GetName().Name ?? throw new InvalidDataException("Missing benchmark assembly name.");
            var project = new FileInfo(Path.Combine(VerificationGate.FindCheckout(), "benchmarks", name, name + ".csproj"));
            return project.Exists ? project : throw new InvalidDataException("Cannot locate the live benchmark project: " + project.FullName);
        }

        protected override void GenerateProject(BuildPartition partition, ArtifactsPaths paths, ILogger logger)
        {
            base.GenerateProject(partition, paths, logger);
            var project = XDocument.Load(paths.ProjectFilePath);
            var root = project.Root ?? throw new InvalidDataException("Generated project has no root.");
            var ns = root.Name.Namespace;
            // Rebuilding project references changes PE/PDB identity under BDN's output paths.
            // Reference the qualified assemblies instead; execution still uses the full evidence gate.
            root.Descendants(ns + "ProjectReference").Remove();
            var references = new XElement(ns + "ItemGroup");
            var assets = new List<BenchmarkAsset>();
            var directory = Path.GetDirectoryName(partition.AssemblyLocation) ?? throw new InvalidDataException("Missing benchmark binary directory.");
            var frozenRoot = Path.Combine(paths.BuildArtifactsDirectoryPath, "qualified-binaries");
            EvidenceFiles.CreateDirectory(frozenRoot);
            foreach (var path in Directory.EnumerateFiles(directory, "*.dll").Order(StringComparer.Ordinal))
            {
                var bytes = File.ReadAllBytes(path);
                using var stream = new MemoryStream(bytes, writable: false);
                using var pe = new PEReader(stream);
                if (!pe.HasMetadata) continue;
                var name = Path.GetFileName(path);
                var frozen = Path.Combine(frozenRoot, name);
                EvidenceFiles.WriteBytes(frozen, bytes);
                references.Add(new XElement(ns + "Reference", new XAttribute("Include", Path.GetFileNameWithoutExtension(path)),
                    new XElement(ns + "HintPath", Path.GetRelativePath(paths.BuildArtifactsDirectoryPath, frozen)),
                    new XElement(ns + "Private", "true")));
                assets.Add(new(name, Blob.Hash(bytes)));
            }
            root.Add(references);
            project.Save(paths.ProjectFilePath);
            EvidenceFiles.WriteJson(Path.Combine(paths.BuildArtifactsDirectoryPath, "qualified-assets.json"), assets);
        }
    }
}
