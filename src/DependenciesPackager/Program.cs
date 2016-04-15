// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Microsoft.DotNet.ProjectModel;
using Microsoft.DotNet.ProjectModel.Compilation;
using Microsoft.Extensions.CommandLineUtils;
using Microsoft.Extensions.Logging;
using NuGet.Frameworks;

namespace DependenciesPackager
{
    class Program
    {
        private static readonly string TempRestoreFolderName = "TempRestorePackages";
        private readonly IEnumerable<string> Runtimes = new[] { "x86", "x64" };
        private readonly IEnumerable<FrameworkConfiguration> FrameworkConfigurations = new[] {
            new FrameworkConfiguration
            {
                Identifier = FrameworkConstants.CommonFrameworks.Net451,
                ShouldCrossgen = false
            },
            new FrameworkConfiguration
            {
                Identifier = FrameworkConstants.CommonFrameworks.NetStandardApp15,
                ShouldCrossgen = true
            }
        };

        private class FrameworkConfiguration
        {
            public NuGetFramework Identifier { get; set; }
            public bool ShouldCrossgen { get; set; }
        }

        private const int Ok = 0;
        private const int Error = 1;

        private ILogger _logger;

        private CommandLineApplication _app;
        private CommandOption _projectJson;
        private CommandOption _diagnosticsProjectJson;
        private CommandOption _sourceFolders;
        private CommandOption _fallbackFeeds;
        private CommandOption _destination;
        private CommandOption _publishPath;
        private CommandOption _version;
        private CommandOption _cliPath;

        public ILogger Logger
        {
            get
            {
                if (_logger == null)
                {
                    _logger = CreateLogger();
                }

                return _logger;
            }
        }

        private ILogger CreateLogger()
        {
            var loggerFactory = new LoggerFactory();
            var logLevel = LogLevel.Information;

            loggerFactory.AddConsole(logLevel, includeScopes: false);

            return loggerFactory.CreateLogger<Program>();
        }

        public Program(
            CommandLineApplication app,
            CommandOption projectJson,
            CommandOption diagnosticsProjectJson,
            CommandOption sourceFolders,
            CommandOption fallbackFeeds,
            CommandOption destination,
            CommandOption publishPath,
            CommandOption packagesVersion,
            CommandOption cliPath)
        {
            _app = app;
            _projectJson = projectJson;
            _diagnosticsProjectJson = diagnosticsProjectJson;
            _sourceFolders = sourceFolders;
            _fallbackFeeds = fallbackFeeds;
            _destination = destination;
            _publishPath = publishPath;
            _version = packagesVersion;
            _cliPath = cliPath;
        }

        static int Main(string[] args)
        {
            var app = new CommandLineApplication();
            app.Name = "DependenciesPackager";

            app.HelpOption("-?|-h|--help");

            var projectJson = app.Option(
                "--project <PATH>",
                "The path to the project.json file from which to perform dotnet restore",
                CommandOptionType.SingleValue);

            var diagnosticsProjectJson = app.Option(
                "--diagnostics-project <PATH>",
                "The path to the project.json file from which to perform dotnet restore for diagnostic purposes",
                CommandOptionType.SingleValue);

            var sourceFolders = app.Option(
                "--sources <DIRS>",
                "Path to the directories containing the nuget packages",
                CommandOptionType.MultipleValue);

            var fallbackFeeds = app.Option(
                "--fallback <URLS>",
                "The list of URLs to use as fallback feeds in case the package can't be found on the source folders",
                CommandOptionType.MultipleValue);

            var destination = app.Option(
                "--destination <DIR>",
                "The folder on which the artifacts will be produced",
                CommandOptionType.SingleValue);

            var publishPath = app.Option(
                "--publish <DIR>",
                "The folder on which the dlls will be published for crossgen",
                CommandOptionType.SingleValue);

            var packagesVersion = app.Option(
                "--version <VERSION>",
                "The version of the artifacts produced",
                CommandOptionType.SingleValue);

            var useCli = app.Option(
                "--use-cli <PATH>",
                "The path to the dotnet CLI tools to use",
                CommandOptionType.SingleValue);

            var program = new Program(
                app,
                projectJson,
                diagnosticsProjectJson,
                sourceFolders,
                fallbackFeeds,
                destination,
                publishPath,
                packagesVersion,
                useCli);

            app.OnExecute(new Func<int>(program.Execute));

            return app.Execute(args);
        }

        private int Execute()
        {
            try
            {
                if (!_projectJson.HasValue() ||
                    !_sourceFolders.HasValue() ||
                    !_fallbackFeeds.HasValue() ||
                    !_destination.HasValue() ||
                    !_version.HasValue())
                {
                    _app.ShowHelp();
                    return Error;
                }

                if (_diagnosticsProjectJson.HasValue())
                {
                    RunDotnetRestore(_diagnosticsProjectJson.Value());
                }

                if (RunDotnetRestore(_projectJson.Value()) != 0)
                {
                    throw new InvalidOperationException("Error restoring nuget packages into destination folder");
                }

                var project = ProjectReader.GetProject(_projectJson.Value());

                foreach (var runtime in Runtimes)
                {
                    var cacheBasePath = Path.Combine(_destination.Value(), _version.Value(), runtime);

                    foreach (var framework in FrameworkConfigurations)
                    {
                        var projectContext = CreateProjectContext(project, framework.Identifier, runtime);
                        var entries = GetEntries(projectContext, runtime);

                        Logger.LogInformation($"Creating hive for {runtime}");

                        if (framework.ShouldCrossgen)
                        {
                            Logger.LogInformation($"Performing crossgen on {framework.Identifier.GetShortFolderName()} for runtime {runtime}.");

                            var publishFolderPath = GetPublishFolderPath(projectContext);
                            CreatePublishFolder(entries, publishFolderPath);
                            var crossGenPath = CopyCrossgenToPublishFolder(entries, $"win7-{runtime}", publishFolderPath);
                            RunCrossGenOnEntries(entries, publishFolderPath, crossGenPath, cacheBasePath);
                            DisplayCrossGenOutput(entries);
                        }
                        else
                        {
                            Logger.LogInformation($"{framework.Identifier.GetShortFolderName()} does not require crossgen, copy files instead.");
                            CopyEntriesToCache(entries, cacheBasePath);
                        }

                        CopyPackageSignatures(entries, cacheBasePath);
                    }

                    CompareWithRestoreHive(Path.Combine(_destination.Value(), TempRestoreFolderName), cacheBasePath);
                    PrintHiveFilesForDiagnostics(cacheBasePath);
                }

                CreateZipPackage();

                return Ok;
            }
            catch (Exception e)
            {
                Logger.LogError(e.Message);
                _app.ShowHelp();
                return Error;
            }
        }

        private void PrintHiveFilesForDiagnostics(string cacheBasePath)
        {
            Logger.LogInformation($@"Files in hive:
{string.Join($"{Environment.NewLine}    ", Directory.EnumerateFiles(cacheBasePath, "*", SearchOption.AllDirectories))}");
        }

        private void CompareWithRestoreHive(string restoredCache, string hivePath)
        {
            var dlls = Directory.EnumerateFiles(restoredCache, "*.dll", SearchOption.AllDirectories);
            var signatures = Directory.EnumerateFiles(restoredCache, "*.sha512", SearchOption.AllDirectories);
            var filesInRestoredCache = new HashSet<string>(
                dlls.Concat(signatures).Select(p => p.Remove(0, restoredCache.Length + 1)));

            var hiveFilePaths = Directory.EnumerateFiles(hivePath, "*", SearchOption.AllDirectories);
            var filesInHive = new HashSet<string>(hiveFilePaths.Select(p => p.Remove(0, hivePath.Length + 1)));

            var filesNotInHive = filesInRestoredCache.Except(filesInHive, StringComparer.OrdinalIgnoreCase);
            foreach (var file in filesNotInHive)
            {
                Logger.LogWarning($@"The file 
    {Path.Combine(restoredCache, file)} can not be found in
    {Path.Combine(hivePath, file)}");
            }
        }

        private static void CopyPackageSignatures(PackageEntry[] entries, string cacheBasePath)
        {
            foreach (var entry in entries.Where(e => e.Library is PackageDescription))
            {
                var packageDescription = (PackageDescription)entry.Library;
                var hash = packageDescription.PackageLibrary.Files.Single(f => f.EndsWith(".sha512"));
                File.Copy(
                    Path.Combine(entry.Library.Path, hash),
                    Path.Combine(
                        cacheBasePath,
                        entry.Library.Identity.Name,
                        entry.Library.Identity.Version.ToNormalizedString(),
                        hash),
                    overwrite: true);
            }
        }

        private void CopyEntriesToCache(PackageEntry[] entries, string cacheBasePath)
        {
            foreach (var package in entries)
            {
                foreach (var asset in package.Assets)
                {
                    var directoryPath = CreateSubDirectory(cacheBasePath, package, asset);
                    var targetPath = Path.Combine(directoryPath, asset.FileName);

                    Logger.LogInformation($@"Copying file 
    {asset.ResolvedPath} into
    {targetPath}");

                    File.Copy(asset.ResolvedPath, targetPath, overwrite: true);
                }
            }
        }

        private void DisplayCrossGenOutput(PackageEntry[] entries)
        {
            var errorMessage = new StringBuilder();
            errorMessage.AppendLine("Output of running crossgen of assemblies:");
            foreach (var package in entries)
            {
                errorMessage.AppendLine($"    Output for assets in {package.Library.Identity}");
                foreach (var entry in package.CrossGenOutput)
                {
                    errorMessage.AppendLine($"    Output for asset {entry.Key.ResolvedPath}");
                    foreach (var line in entry.Value)
                    {
                        errorMessage.AppendLine(line);
                    }
                }
            }

            Logger.LogInformation(errorMessage.ToString());
        }

        private void RunCrossGenOnEntries(PackageEntry[] entries, string publishFolderPath, string crossGenPath, string cacheBasePath)
        {
            foreach (var package in entries)
            {
                foreach (var asset in package.Assets)
                {
                    var subDirectory = CreateSubDirectory(cacheBasePath, package, asset);
                    var targetPath = Path.Combine(subDirectory, asset.FileName);
                    var succeeded = RunCrossGenOnAssembly(crossGenPath, publishFolderPath, package, asset, targetPath);

                    if (!succeeded)
                    {
                        Logger.LogWarning("Copying non crossgen asset instead.");
                        File.Copy(asset.ResolvedPath, targetPath, overwrite: true);
                    }
                }
            }
        }

        private string CreateSubDirectory(string cacheBasePath, PackageEntry package, LibraryAsset asset)
        {
            var subDirectoryPath = Path.Combine(
                cacheBasePath,
                package.Library.Identity.Name,
                package.Library.Identity.Version.ToNormalizedString(),
                Path.GetDirectoryName(asset.RelativePath));

            Logger.LogInformation($"Creating sub directory on {subDirectoryPath}.");

            var subDirectory = Directory.CreateDirectory(subDirectoryPath);
            return subDirectory.FullName;
        }

        private string GetPublishFolderPath(ProjectContext projectContext)
        {
            return Path.Combine(
                _publishPath.Value(),
                projectContext.TargetFramework.GetShortFolderName(),
                projectContext.RuntimeIdentifier);
        }

        private void CreatePublishFolder(PackageEntry[] entries, string publishFolder)
        {
            Logger.LogInformation($"Creating directory {publishFolder}.");
            Directory.CreateDirectory(publishFolder);

            foreach (var package in entries)
            {
                foreach (var asset in package.Assets)
                {
                    var path = asset.ResolvedPath;
                    var targetPath = Path.Combine(publishFolder, asset.FileName);

                    Logger.LogInformation($@"Copying file 
    {path} into
    {targetPath}");
                    File.Copy(path, targetPath, overwrite: true);
                }
            }
        }

        private PackageEntry[] GetEntries(ProjectContext context, string runtime)
        {
            var restoreCache = Path.Combine(_destination.Value(), TempRestoreFolderName);
            return context
                .CreateExporter("CACHE")
                .GetDependencies()
                .Select(d => new PackageEntry
                {
                    Library = d.Library,
                    Assets = (d.RuntimeAssemblyGroups.SingleOrDefault(rg => rg.Runtime == $"win7-{runtime}") ??
                              d.RuntimeAssemblyGroups.SingleOrDefault(rg => rg.Runtime == ""))?.Assets
                })
                .Where(e =>
                    e.Library?.Path?.StartsWith(restoreCache, StringComparison.OrdinalIgnoreCase) != null &&
                    e.Assets?.Any() == true)
                .ToArray();
        }

        private ProjectContext CreateProjectContext(Project project, NuGetFramework framework, string runtime)
        {
            var builder = new ProjectContextBuilder()
                .WithPackagesDirectory(Path.Combine(_destination.Value(), TempRestoreFolderName))
                .WithProject(project)
                .WithTargetFramework(framework)
                .WithRuntimeIdentifiers(new[] { $"win7-{runtime}" });

            return builder.Build();
        }

        private bool RunCrossGenOnAssembly(
            string crossGenPath,
            string publishedAssemblies,
            PackageEntry package,
            LibraryAsset asset,
            string targetPath)
        {
            var assemblyPath = asset.ResolvedPath;

            var arguments = new[]
            {
                $"/Platform_Assemblies_Paths {publishedAssemblies}",
                $"/in {assemblyPath}",
                $"/out {targetPath}"
            };

            Logger.LogInformation($@"Running crossgen on 
    {assemblyPath} and putting results on 
    {targetPath} with arguments
        {string.Join($"{Environment.NewLine}        ", arguments)}.");

            Environment.CurrentDirectory = publishedAssemblies;
            StringBuilder output = new StringBuilder();
            StringBuilder error = new StringBuilder();

            var exitCode = new ProcessRunner(crossGenPath, string.Join(" ", arguments))
                .WriteOutputToStringBuilder(output, "    ")
                .WriteErrorsToStringBuilder(error, "    ")
                .Run();

            package.CrossGenOutput.Add(asset, new List<string> { output.ToString(), error.ToString() });

            if (exitCode == 0)
            {
                Logger.LogInformation($"Native image {targetPath} generated successfully.");
                return true;
            }
            else
            {
                Logger.LogWarning($"Crossgen failed for {targetPath}.");
                return false;
            }
        }

        private string CopyCrossgenToPublishFolder(
            IEnumerable<PackageEntry> packages,
            string moniker,
            string publishDirectory)
        {
            var entry = packages.SingleOrDefault(p =>
                p.Library.Identity.Name.Equals(
                    $"runtime.{moniker}.Microsoft.NETCore.Runtime.CoreCLR",
                    StringComparison.OrdinalIgnoreCase));

            if (entry == null)
            {
                throw new InvalidOperationException("Couldn't find the dependency 'Microsoft.NETCore.Runtime.CoreCLR'");
            }

            var path = Directory
                .GetFiles(entry.Library.Path, "crossgen.exe", SearchOption.AllDirectories)
                .SingleOrDefault();

            if (path == null)
            {
                throw new InvalidOperationException("Couldn't find path to crossgen.exe");
            }

            var crossGenDirectory = Path.GetDirectoryName(path);
            foreach (var file in Directory.GetFiles(crossGenDirectory))
            {
                File.Copy(file, Path.Combine(publishDirectory, Path.GetFileName(file)), overwrite: true);
            }

            return Path.Combine(publishDirectory, Path.GetFileName(path));
        }

        private void CreateZipPackage()
        {
            var packagesFolder = Path.Combine(_destination.Value(), _version.Value());
            var zipFileName = Path.Combine(_destination.Value(), $"AspNetCore.{_version.Value()}.zip");
            Logger.LogInformation($"Creating zip package on {zipFileName}");
            ZipFile.CreateFromDirectory(packagesFolder, zipFileName);
        }

        private int RunDotnetRestore(string projectJson)
        {
            Logger.LogInformation($"Running dotnet restore on {Path.Combine(_destination.Value(), TempRestoreFolderName)}");
            var sources = string.Join(" ", _sourceFolders.Values.Select(v => $"--source {v}"));
            var fallbackFeeds = string.Join(" ", _fallbackFeeds.Values.Select(v => $"--fallbacksource {v} "));
            var packages = $"--packages {Path.Combine(_destination.Value(), TempRestoreFolderName)}";

            var dotnet = _cliPath.HasValue() ? _cliPath.Value() : "dotnet";

            var arguments = string.Join(
                " ",
                "restore",
                packages,
                projectJson,
                sources,
                fallbackFeeds,
                "--verbosity Verbose");

            return new ProcessRunner(dotnet, arguments)
                .WriteOutputToConsole()
                .WriteOutputToConsole()
                .Run();
        }

        private class ProcessRunner
        {
            private readonly string _arguments;
            private readonly string _exePath;
            private readonly IDictionary<string, string> _environment = new Dictionary<string, string>();
            private Process _process = null;

            public ProcessRunner(
                string exePath,
                string arguments)
            {
                _exePath = exePath;
                _arguments = arguments;
            }

            public Action<string> OnError { get; set; } = s => { };
            public Action<string> OnOutput { get; set; } = s => { };

            public int ExitCode => _process.ExitCode;

            public ProcessRunner WriteErrorsToConsole()
            {
                OnError = s => Console.WriteLine(s);
                return this;
            }

            public ProcessRunner WriteOutputToConsole()
            {
                OnOutput = s => Console.WriteLine(s);
                return this;
            }

            public ProcessRunner WriteErrorsToStringBuilder(StringBuilder builder, string indentation)
            {
                OnError = s => builder.AppendLine(indentation + s);
                return this;
            }

            public ProcessRunner WriteOutputToStringBuilder(StringBuilder builder, string indentation)
            {
                OnOutput = s => builder.AppendLine(indentation + s);
                return this;
            }

            public ProcessRunner AddEnvironmentVariable(string name, string value)
            {
                _environment.Add(name, value);
                return this;
            }

            public int Run()
            {
                if (_process != null)
                {
                    throw new InvalidOperationException("The process has already been started.");
                }

                ProcessStartInfo processInfo = CreateProcessInfo();
                _process = new Process();
                _process.StartInfo = processInfo;
                _process.EnableRaisingEvents = true;
                _process.ErrorDataReceived += (s, e) => OnError(e.Data);
                _process.OutputDataReceived += (s, e) => OnOutput(e.Data);
                _process.Start();

                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();

                _process.WaitForExit();
                return _process.ExitCode;
            }

            private ProcessStartInfo CreateProcessInfo()
            {
                var processInfo = new ProcessStartInfo(_exePath, _arguments);
                foreach (var variable in _environment)
                {
                    processInfo.EnvironmentVariables.Add(variable.Key, variable.Value);
                }

                processInfo.CreateNoWindow = true;
                processInfo.UseShellExecute = false;
                processInfo.RedirectStandardError = true;
                processInfo.RedirectStandardOutput = true;

                return processInfo;
            }
        }

        private class PackageEntry
        {
            public IReadOnlyList<LibraryAsset> Assets { get; set; }
            public LibraryDescription Library { get; set; }

            public IDictionary<LibraryAsset, IList<string>> CrossGenOutput { get; } =
                new Dictionary<LibraryAsset, IList<string>>();
        }
    }
}