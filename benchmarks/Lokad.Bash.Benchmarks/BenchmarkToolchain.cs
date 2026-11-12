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
using BenchmarkDotNet.Validators;

namespace Lokad.Bash.Benchmarks;

internal sealed record BenchmarkAsset(string FileName, string Sha256);

/// <summary>Builds the timing wrapper against the exact binaries that supplied correctness evidence.</summary>
internal static class BenchmarkToolchain
{
    public static IToolchain Create(string framework)
    {
        var standard = CsProjCoreToolchain.From(new NetCoreAppSettings(framework, null, framework));
        return new CompiledReferences(standard, new CompiledGenerator(framework));
    }

    private sealed class CompiledReferences(IToolchain standard, IGenerator generator) : IToolchain
    {
        public string Name => "Qualified binaries " + standard.Name;
        public IGenerator Generator => generator;
        public IBuilder Builder => standard.Builder;
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

    private sealed class CompiledGenerator(string framework) : CsProjGenerator(framework, "", "", "", true)
    {
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
            foreach (var path in Directory.EnumerateFiles(directory, "*.dll").Order(StringComparer.Ordinal))
            {
                using var stream = File.OpenRead(path);
                using var pe = new PEReader(stream);
                if (!pe.HasMetadata) continue;
                references.Add(new XElement(ns + "Reference", new XAttribute("Include", Path.GetFileNameWithoutExtension(path)),
                    new XElement(ns + "HintPath", path), new XElement(ns + "Private", "true")));
                assets.Add(new(Path.GetFileName(path), Blob.Hash(File.ReadAllBytes(path))));
            }
            root.Add(references);
            project.Save(paths.ProjectFilePath);
            File.WriteAllBytes(Path.Combine(paths.BuildArtifactsDirectoryPath, "qualified-assets.json"),
                JsonSerializer.SerializeToUtf8Bytes(assets, WorkloadCatalog.Json));
        }
    }
}
