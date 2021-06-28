// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Dotnet.Integration.Test.Utils;
using Newtonsoft.Json.Linq;
using NuGet.Commands;
using NuGet.Common;
using NuGet.Frameworks;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Test.Utility;
using NuGet.Versioning;
using NuGet.XPlat.FuncTest;
using Xunit;

namespace Dotnet.Integration.Test
{
    public class MsbuildIntegrationTestFixture : IDisposable
    {
        private readonly TestDirectory _cliDirectory;
        private readonly SimpleTestPathContext _templateDirectory;
        private readonly string _dotnetCli = DotnetCliUtil.GetDotnetCli();
        internal readonly string TestDotnetCli;
        internal readonly string MsBuildSdksPath;
        internal string SdkVersion { get; private set; }
        private readonly Dictionary<string, string> _processEnvVars = new Dictionary<string, string>();

        public MsbuildIntegrationTestFixture()
        {
            _cliDirectory = CopyLatestCliForPack();
            var dotnetExecutableName = RuntimeEnvironmentHelper.IsWindows ? "dotnet.exe" : "dotnet";
            TestDotnetCli = Path.Combine(_cliDirectory, dotnetExecutableName);

            var sdkPath = Directory.EnumerateDirectories(Path.Combine(_cliDirectory, "sdk"))
                            .Where(d => !string.Equals(Path.GetFileName(d), "NuGetFallbackFolder", StringComparison.OrdinalIgnoreCase))
                            .Single();

            MsBuildSdksPath = Path.Combine(sdkPath, "Sdks");

            _templateDirectory = new SimpleTestPathContext();
            WriteGlobalJson(_templateDirectory.WorkingDirectory);

            // some project templates use implicit packages. For example, class libraries targeting netstandard2.0
            // will have an implicit package reference for NETStandard.Library, and its dependencies.
            // .NET Core SDK 3.0 and later no longer ship these packages in a NuGetFallbackFolder. Therefore, we need
            // to be able to download these packages. We'll download it once into the template cache's global packages
            // folder, and then use that as a local source for individual tests, to minimise network access.
            var addSourceArgs = new AddSourceArgs()
            {
                Configfile = _templateDirectory.NuGetConfig,
                Name = "nuget.org",
                Source = "https://api.nuget.org/v3/index.json"
            };
            AddSourceRunner.Run(addSourceArgs, () => NullLogger.Instance);

            _processEnvVars.Add("MSBuildSDKsPath", MsBuildSdksPath);
            _processEnvVars.Add("UseSharedCompilation", "false");
            _processEnvVars.Add("DOTNET_MULTILEVEL_LOOKUP", "0");
            _processEnvVars.Add("MSBUILDDISABLENODEREUSE ", "true");
        }

        /// <summary>
        /// Creates a new dotnet project of the specified type. Note that restore/build are not run when this command is invoked.
        /// That is because the project generation is cached.
        /// </summary>
        internal void CreateDotnetNewProject(string solutionRoot, string projectName, string args = "console", int timeOut = 60000)
        {
            args = args.Trim();
            var workingDirectory = Path.Combine(solutionRoot, projectName);
            if (!Directory.Exists(workingDirectory))
            {
                Directory.CreateDirectory(workingDirectory);
            }
            var templateDirectory = new DirectoryInfo(Path.Combine(_templateDirectory.SolutionRoot, args));

            if (!templateDirectory.Exists)
            {
                templateDirectory.Create();

                var result = CommandRunner.Run(TestDotnetCli,
                    templateDirectory.FullName,
                    $"new {args}",
                    waitForExit: true,
                    timeOutInMilliseconds: timeOut,
                    environmentVariables: _processEnvVars);
                Assert.True(result.Success, $"Creating project failed with following log information :\n {result.AllOutput}");
                Assert.True(string.IsNullOrWhiteSpace(result.Errors), $"Creating project failed with following message in error stream :\n {result.AllOutput}");
                // Delete the obj directory because it contains assets generated by running restore at dotnet new <template> time.
                // These are not relevant when the project is renamed
                Directory.Delete(Path.Combine(templateDirectory.FullName, "obj"), recursive: true);
            }
            CopyFromTemplate(projectName, args, workingDirectory, templateDirectory);
        }

        private static void CopyFromTemplate(string projectName, string args, string workingDirectory, DirectoryInfo templateDirectoryInfo)
        {
            foreach (var file in Directory.EnumerateFiles(templateDirectoryInfo.FullName))
            {
                File.Copy(file, Path.Combine(workingDirectory, Path.GetFileName(file)));
            }
            File.Move(
                Path.Combine(workingDirectory, args + ".csproj"),
                Path.Combine(workingDirectory, projectName + ".csproj"));
        }

        internal void CreateDotnetToolProject(string solutionRoot, string projectName, string targetFramework, string rid, string packageSources = null, IList<PackageIdentity> packages = null, int timeOut = 60000)
        {
            var workingDirectory = Path.Combine(solutionRoot, projectName);
            if (!Directory.Exists(workingDirectory))
            {
                Directory.CreateDirectory(workingDirectory);
            }

            var projectFileName = Path.Combine(workingDirectory, projectName + ".csproj");

            packageSources ??= string.Empty;
            var restorePackagesPath = Path.Combine(workingDirectory, "tools", "packages");
            var restoreSolutionDirectory = workingDirectory;
            var msbuildProjectExtensionsPath = Path.Combine(workingDirectory);
            var packageReferences = string.Empty;

            if (packages != null)
            {
                packageReferences = string.Join(Environment.NewLine, packages.Select(p => $@"        <PackageReference Include='{p.Id}' Version='{p.Version}'/>"));
            }

            var projectFile = $@"<Project>
    <PropertyGroup>
        <!-- Things that do change and before common props -->
        <MSBuildProjectExtensionsPath>{msbuildProjectExtensionsPath}</MSBuildProjectExtensionsPath>
    </PropertyGroup>
    <!-- Import it via Sdk attribute for local testing -->
    <Import Sdk='Microsoft.NET.Sdk' Project='Sdk.props'/>
    <PropertyGroup>
        <OutputType>Exe</OutputType>
        <RuntimeIdentifier>{rid}</RuntimeIdentifier>
        <TargetFramework>{targetFramework}</TargetFramework>
        <RestoreProjectStyle>DotnetToolReference</RestoreProjectStyle>
        <!-- Things that do change -->
        <RestoreSources>{packageSources}</RestoreSources>
        <RestorePackagesPath>{restorePackagesPath}</RestorePackagesPath>
        <RestoreSolutionDirectory>{restoreSolutionDirectory}</RestoreSolutionDirectory>
        <!--Things that don't change -->
        <RestoreAdditionalProjectSources/>
        <RestoreAdditionalProjectFallbackFolders/>
        <RestoreAdditionalProjectFallbackFoldersExcludes/>
        <RestoreFallbackFolders>clear</RestoreFallbackFolders>
        <CheckEolTargetFramework>false</CheckEolTargetFramework>
        <DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences>
    </PropertyGroup>
    <ItemGroup>
{packageReferences}
    </ItemGroup>
    <Import Sdk='Microsoft.NET.Sdk' Project='Sdk.targets'/>
</Project>";

            try
            {
                File.WriteAllText(projectFileName, projectFile);
            }
            catch
            {
                // ignore
            }
            Assert.True(File.Exists(projectFileName));
        }

        internal CommandRunnerResult RestoreToolProject(string workingDirectory, string projectName, string args = "")
        {
            var result = CommandRunner.Run(TestDotnetCli,
                workingDirectory,
                $"restore {projectName}.csproj {args}",
                waitForExit: true,
                environmentVariables: _processEnvVars);
            return result;
        }

        internal void RestoreProject(string workingDirectory, string projectName, string args, bool validateSuccess = true)
            => RestoreProjectOrSolution(workingDirectory, $"{projectName}.csproj", args, validateSuccess);

        internal void RestoreSolution(string workingDirectory, string solutionName, string args, bool validateSuccess = true)
            => RestoreProjectOrSolution(workingDirectory, $"{solutionName}.sln", args, validateSuccess);

        private void RestoreProjectOrSolution(string workingDirectory, string fileName, string args, bool validateSuccess)
        {
            var result = CommandRunner.Run(TestDotnetCli,
                workingDirectory,
                $"restore {fileName} {args}",
                waitForExit: true,
                environmentVariables: _processEnvVars);
            if (validateSuccess)
            {
                Assert.True(result.Item1 == 0, $"Restore failed with following log information :\n {result.AllOutput}");
                Assert.True(result.Item3 == "", $"Restore failed with following message in error stream :\n {result.AllOutput}");
            }
        }

        /// <summary>
        /// dotnet.exe args
        /// </summary>
        internal CommandRunnerResult RunDotnet(
            string workingDirectory,
            string args,
            bool ignoreExitCode = false,
            IReadOnlyDictionary<string, string> additionalEnvVars = null)
        {
            IDictionary<string, string> envVars;
            if (additionalEnvVars == null)
            {
                envVars = _processEnvVars;
            }
            else
            {
                // GroupBy respects sequence order, so taking the last pair per environment variable name will allow the
                // input dictionary to override the defaults.
                envVars = _processEnvVars
                    .Concat(additionalEnvVars)
                    .GroupBy(x => x.Key, _processEnvVars.Comparer)
                    .ToDictionary(x => x.Key, x => x.Last().Value);
            }

            var result = CommandRunner.Run(TestDotnetCli,
                workingDirectory,
                args,
                waitForExit: true,
                environmentVariables: envVars);

            if (!ignoreExitCode)
            {
                Assert.True(result.ExitCode == 0, $"dotnet.exe {args} command failed with following log information :\n {result.AllOutput}");
            }

            return result;
        }

        internal CommandRunnerResult PackProject(string workingDirectory, string projectName, string args, string nuspecOutputPath = "obj", bool validateSuccess = true)
        {
            // We can't provide empty or spaces as arguments if we used `string.IsNullOrEmpty` or `string.IsNullOrWhiteSpace`.
            if (nuspecOutputPath != null)
            {
                args = $"{args} /p:NuspecOutputPath={nuspecOutputPath}";
            }
            return PackProjectOrSolution(workingDirectory, $"{projectName}.csproj", args, validateSuccess);
        }

        internal CommandRunnerResult PackSolution(string workingDirectory, string solutionName, string args, string nuspecOutputPath = "obj", bool validateSuccess = true)
        {
            if (nuspecOutputPath != null)
            {
                args = $"{args} /p:NuspecOutputPath={nuspecOutputPath}";
            }
            return PackProjectOrSolution(workingDirectory, $"{solutionName}.sln", args, validateSuccess);
        }

        private CommandRunnerResult PackProjectOrSolution(string workingDirectory, string file, string args, bool validateSuccess)
        {
            var result = CommandRunner.Run(TestDotnetCli,
                workingDirectory,
                $"pack {file} {args}",
                waitForExit: true,
                environmentVariables: _processEnvVars);
            if (validateSuccess)
            {
                Assert.True(result.Item1 == 0, $"Pack failed with following log information :\n {result.AllOutput}");
                Assert.True(result.Item3 == "", $"Pack failed with following message in error stream :\n {result.AllOutput}");
            }
            return result;
        }

        internal void BuildProject(string workingDirectory, string projectName, string args, bool? appendRidToOutputPath = false, bool validateSuccess = true)
        {
            if (appendRidToOutputPath != null)
            {
                args = $"{args} /p:AppendRuntimeIdentifierToOutputPath={appendRidToOutputPath}";
            }
            BuildProjectOrSolution(workingDirectory, $"{projectName}.csproj", args, validateSuccess);
        }

        internal void BuildSolution(string workingDirectory, string solutionName, string args, bool? appendRidToOutputPath = false, bool validateSuccess = true)
        {
            if (appendRidToOutputPath != null)
            {
                args = $"{args} /p:AppendRuntimeIdentifierToOutputPath={appendRidToOutputPath}";
            }
            BuildProjectOrSolution(workingDirectory, $"{solutionName}.sln", args, validateSuccess);
        }

        private void BuildProjectOrSolution(string workingDirectory, string file, string args, bool validateSuccess)
        {
            var result = CommandRunner.Run(TestDotnetCli,
                workingDirectory,
                $"msbuild {file} {args}",
                waitForExit: true,
                environmentVariables: _processEnvVars);
            if (validateSuccess)
            {
                Assert.True(result.Item1 == 0, $"Build failed with following log information :\n {result.AllOutput}");
                Assert.True(result.Item3 == "", $"Build failed with following message in error stream :\n {result.AllOutput}");
            }
        }

        internal TestDirectory CreateTestDirectory()
        {
            var testDirectory = TestDirectory.Create();

            WriteGlobalJson(testDirectory);

            return testDirectory;
        }

        internal SimpleTestPathContext CreateSimpleTestPathContext()
        {
            var simpleTestPathContext = new SimpleTestPathContext();

            WriteGlobalJson(simpleTestPathContext.WorkingDirectory);

            // Some template and TFM combinations need packages, for example NETStandard.Library.
            // The template cache should have downloaded it already, so use the template cache's
            // global packages folder as a local source.
            var addSourceArgs = new AddSourceArgs()
            {
                Configfile = simpleTestPathContext.NuGetConfig,
                Name = "template",
                Source = _templateDirectory.UserPackagesFolder
            };
            AddSourceRunner.Run(addSourceArgs, () => NullLogger.Instance);

            return simpleTestPathContext;
        }

        internal TestDirectory Build(TestDirectoryBuilder testDirectoryBuilder)
        {
            var testDirectory = testDirectoryBuilder.Build();

            WriteGlobalJson(testDirectory);

            return testDirectory;
        }

        private void WriteGlobalJson(string path)
        {
            string globalJsonText = $"{{\"sdk\": {{\"version\": \"{SdkVersion}\"}}}}";
            var globalJsonPath = Path.Combine(path, "global.json");
            File.WriteAllText(globalJsonPath, globalJsonText);
        }

        private TestDirectory CopyLatestCliForPack()
        {
            var cliDirectory = TestDirectory.Create();
            CopyLatestCliToTestDirectory(cliDirectory);
            UpdateCliWithLatestNuGetAssemblies(cliDirectory);
            return cliDirectory;
        }

        private void CopyLatestCliToTestDirectory(string destinationDir)
        {
            var cliDir = Path.GetDirectoryName(_dotnetCli);
            var sdkDir = Path.Combine(cliDir, "sdk" + Path.DirectorySeparatorChar);

            // Determine which SDK version to copy
            SdkVersion = GetSdkToTest(sdkDir);

            WriteGlobalJson(destinationDir);

            var sdkPath = Path.Combine(sdkDir, SdkVersion + Path.DirectorySeparatorChar);
            var fallbackFolderPath = Path.Combine(sdkDir, "NuGetFallbackFolder");

            Func<string, bool> predicate = path =>
            {
                if (!path.StartsWith(sdkDir))
                {
                    return true;
                }

                return path.StartsWith(sdkPath) || path.StartsWith(fallbackFolderPath);
            };

            //Create sub-directory structure in destination, ignoring any SDK version not selected.
            foreach (var directory in Directory.EnumerateDirectories(cliDir, "*", SearchOption.AllDirectories).Where(predicate))
            {
                var destDir = destinationDir + directory.Substring(cliDir.Length);
                if (!Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }
            }

            var lastWriteTime = DateTime.Now.AddDays(-2);

            //Copy files recursively to destination directories, ignoring any SDK version not selected.
            foreach (var fileName in Directory.EnumerateFiles(cliDir, "*", SearchOption.AllDirectories).Where(predicate))
            {
                var destFileName = destinationDir + fileName.Substring(cliDir.Length);
                File.Copy(fileName, destFileName);
                File.SetLastWriteTime(destFileName, lastWriteTime);
            }
        }

        private string GetSdkToTest(string sdkDir)
        {
            // The TFM we're testing
            var testTfm = AssemblyReader.GetTargetFramework(typeof(MsbuildIntegrationTestFixture).Assembly.Location);

            var selectedVersion =
                Directory.EnumerateDirectories(sdkDir) // get all directories in sdk folder
                .Where(path =>
                { // SDK is for TFM to test
                    if (string.Equals(Path.GetFileName(path), "NuGetFallbackFolder", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    var dotnetPath = Path.Combine(path, "dotnet.dll");
                    var sdkTfm = AssemblyReader.GetTargetFramework(dotnetPath);

                    return testTfm == sdkTfm;
                })
                .Select(Path.GetFileName) // just the folder name (version string)
                .OrderByDescending(path => NuGetVersion.Parse(Path.GetFileName(path))) // in case there are multiple matching SDKs, selected the highest version
                .FirstOrDefault();

            if (selectedVersion == null)
            {
                var message = $@"Could not find suitable SDK to test in {sdkDir}
TFM being tested: {testTfm.DotNetFrameworkName}
SDKs found: {string.Join(", ", Directory.EnumerateDirectories(sdkDir).Select(Path.GetFileName).Where(d => !string.Equals(d, "NuGetFallbackFolder", StringComparison.OrdinalIgnoreCase)))}";

                throw new Exception(message);
            }

            return selectedVersion;
        }

        private void UpdateCliWithLatestNuGetAssemblies(string cliDirectory)
        {
            var artifactsDirectory = DotnetCliUtil.GetArtifactsDirectoryInRepo();
            var pathToSdkInCli = Path.Combine(
                    Directory.EnumerateDirectories(Path.Combine(cliDirectory, "sdk"))
                        .Where(d => !string.Equals(Path.GetFileName(d), "NuGetFallbackFolder", StringComparison.OrdinalIgnoreCase))
                        .First());
            const string configuration =
#if DEBUG
                "Debug";
#else
                "Release";
#endif
            CopyPackSdkArtifacts(artifactsDirectory, pathToSdkInCli, configuration);
            CopyRestoreArtifacts(artifactsDirectory, pathToSdkInCli, configuration);
        }

        private void CopyRestoreArtifacts(string artifactsDirectory, string pathToSdkInCli, string configuration)
        {
            const string restoreProjectName = "NuGet.Build.Tasks";
            const string restoreTargetsName = "NuGet.targets";
            const string restoreTargetsExtName = "NuGet.RestoreEx.targets";

            var sdkDependencies = new List<string> { restoreProjectName, "NuGet.Versioning", "NuGet.Protocol", "NuGet.ProjectModel", "NuGet.Packaging", "NuGet.LibraryModel", "NuGet.Frameworks", "NuGet.DependencyResolver.Core", "NuGet.Configuration", "NuGet.Common", "NuGet.Commands", "NuGet.CommandLine.XPlat", "NuGet.Credentials", "NuGet.Build.Tasks.Console" };

            var sdkTfm = AssemblyReader.GetTargetFramework(Path.Combine(pathToSdkInCli, "dotnet.dll"));

            // Copy rest of the NuGet assemblies.
            foreach (var projectName in sdkDependencies)
            {
                var projectArtifactsBinFolder = Path.Combine(artifactsDirectory, projectName, "bin", configuration);

                var tfmToCopy = GetTfmToCopy(sdkTfm, projectArtifactsBinFolder);
                var frameworkArtifactsFolder = new DirectoryInfo(Path.Combine(projectArtifactsBinFolder, tfmToCopy));

                var fileName = projectName + ".dll";
                File.Copy(
                        sourceFileName: Path.Combine(frameworkArtifactsFolder.FullName, fileName),
                        destFileName: Path.Combine(pathToSdkInCli, fileName),
                        overwrite: true);
                // Copy the restore targets.
                if (projectName.Equals(restoreProjectName))
                {
                    File.Copy(
                        sourceFileName: Path.Combine(frameworkArtifactsFolder.FullName, restoreTargetsName),
                        destFileName: Path.Combine(pathToSdkInCli, restoreTargetsName),
                        overwrite: true);
                    File.Copy(
                        sourceFileName: Path.Combine(frameworkArtifactsFolder.FullName, restoreTargetsExtName),
                        destFileName: Path.Combine(pathToSdkInCli, restoreTargetsExtName),
                        overwrite: true);
                }
            }
        }

        private string GetTfmToCopy(NuGetFramework sdkTfm, string projectArtifactsBinFolder)
        {
            var compiledTfms =
                Directory.EnumerateDirectories(projectArtifactsBinFolder) // get all directories in bin folder
                .Select(Path.GetFileName) // just the folder name (tfm)
                .ToDictionary(folder => NuGetFramework.Parse(folder));

            var reducer = new FrameworkReducer();
            var selectedTfm = reducer.GetNearest(sdkTfm, compiledTfms.Keys);

            if (selectedTfm == null)
            {
                var message = $@"Could not find suitable assets to copy in {projectArtifactsBinFolder}
TFM being tested: {sdkTfm}
project TFMs found: {string.Join(", ", compiledTfms.Keys.Select(k => k.ToString()))}";

                throw new Exception(message);
            }

            var selectedVersion = compiledTfms[selectedTfm];

            return selectedVersion;
        }

        private void CopyPackSdkArtifacts(string artifactsDirectory, string pathToSdkInCli, string configuration)
        {
            var pathToPackSdk = Path.Combine(pathToSdkInCli, "Sdks", "NuGet.Build.Tasks.Pack");
            var sdkTfm = AssemblyReader.GetTargetFramework(Path.Combine(pathToSdkInCli, "dotnet.dll"));

            const string packProjectName = "NuGet.Build.Tasks.Pack";
            const string packTargetsName = "NuGet.Build.Tasks.Pack.targets";

            // Copy the pack SDK.
            var packProjectBinDirectory = Path.Combine(artifactsDirectory, packProjectName, "bin", configuration);
            var tfmToCopy = GetTfmToCopy(sdkTfm, packProjectBinDirectory);

            var packProjectCoreArtifactsDirectory = new DirectoryInfo(Path.Combine(packProjectBinDirectory, tfmToCopy));

            // We are only copying the CoreCLR assets, since, we're testing only them under Core MSBuild.
            var targetRuntimeType = "CoreCLR";

            var packAssemblyDestinationDirectory = Path.Combine(pathToPackSdk, targetRuntimeType);
            // Be smart here so we don't have to call ILMerge in the VS build. It takes ~15s total.
            // In VisualStudio, simply use the non il merged version.
            var ilMergedPackDirectoryPath = Path.Combine(packProjectCoreArtifactsDirectory.FullName, "ilmerge");
            if (Directory.Exists(ilMergedPackDirectoryPath))
            {
                var packFileName = packProjectName + ".dll";
                // Only use the il merged assembly if it's newer than the build.
                DateTime packAssemblyCreationDate = File.GetCreationTimeUtc(Path.Combine(packProjectCoreArtifactsDirectory.FullName, packFileName));
                DateTime ilMergedPackAssemblyCreationDate = File.GetCreationTimeUtc(Path.Combine(ilMergedPackDirectoryPath, packFileName));
                if (ilMergedPackAssemblyCreationDate > packAssemblyCreationDate)
                {
                    FileUtility.Replace(
                        sourceFileName: Path.Combine(packProjectCoreArtifactsDirectory.FullName, "ilmerge", packFileName),
                        destFileName: Path.Combine(packAssemblyDestinationDirectory, packFileName));
                }
            }
            else
            {
                foreach (var assembly in packProjectCoreArtifactsDirectory.EnumerateFiles("*.dll"))
                {
                    File.Copy(
                        sourceFileName: assembly.FullName,
                        destFileName: Path.Combine(packAssemblyDestinationDirectory, assembly.Name),
                        overwrite: true);
                }
            }
            // Copy the pack targets
            var packTargetsSource = Path.Combine(packProjectCoreArtifactsDirectory.FullName, packTargetsName);
            var targetsDestination = Path.Combine(pathToPackSdk, "build", packTargetsName);
            var targetsDestinationCrossTargeting = Path.Combine(pathToPackSdk, "buildCrossTargeting", packTargetsName);
            File.Copy(packTargetsSource, targetsDestination, overwrite: true);
            File.Copy(packTargetsSource, targetsDestinationCrossTargeting, overwrite: true);
        }

        public void Dispose()
        {
            RunDotnet(Path.GetDirectoryName(TestDotnetCli), "build-server shutdown");
            KillDotnetExe(TestDotnetCli);
            _cliDirectory.Dispose();
            _templateDirectory.Dispose();
        }

        private static void KillDotnetExe(string pathToDotnetExe)
        {
            var processes = Process.GetProcessesByName("dotnet")
                .Where(t => string.Compare(t.MainModule.FileName, Path.GetFullPath(pathToDotnetExe), ignoreCase: true) == 0);
            var testDirProcesses = Process.GetProcesses()
                .Where(t => t.MainModule.FileName.StartsWith(TestFileSystemUtility.NuGetTestFolder, StringComparison.OrdinalIgnoreCase));
            try
            {
                if (processes != null)
                {
                    foreach (var process in processes)
                    {
                        if (string.Compare(process.MainModule.FileName, Path.GetFullPath(pathToDotnetExe), true) == 0)
                        {
                            process.Kill();
                        }
                    }
                }

                if (testDirProcesses != null)
                {
                    foreach (var process in testDirProcesses)
                    {
                        process.Kill();
                    }
                }

            }
            catch { }
        }

        /// <summary>
        /// Depth-first recursive delete, with handling for descendant
        /// directories open in Windows Explorer or used by another process
        /// </summary>
        private static void DeleteDirectory(string path)
        {
            foreach (string directory in Directory.EnumerateDirectories(path))
            {
                DeleteDirectory(directory);
            }

            try
            {
                Directory.Delete(path, true);
            }
            catch (IOException)
            {
                Directory.Delete(path, true);
            }
            catch (UnauthorizedAccessException)
            {
                var MaxTries = 100;

                for (var i = 0; i < MaxTries; i++)
                {

                    try
                    {
                        Directory.Delete(path, recursive: true);
                        break;
                    }
                    catch (UnauthorizedAccessException) when (i < (MaxTries - 1))
                    {
                        Thread.Sleep(100);
                    }
                }
            }
            catch
            {

            }
        }
    }
}
