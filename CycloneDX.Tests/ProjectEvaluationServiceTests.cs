// This file is part of CycloneDX Tool for .NET
//
// Licensed under the Apache License, Version 2.0 (the “License”);
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an “AS IS” BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) OWASP Foundation. All Rights Reserved.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Linq;
using CycloneDX.Interfaces;
using CycloneDX.Models;
using CycloneDX.Services;
using Moq;
using Xunit;
using XFS = System.IO.Abstractions.TestingHelpers.MockUnixSupport;

namespace CycloneDX.Tests
{
    public sealed class ProjectEvaluationServiceTests : IDisposable
    {
        private static readonly string[] PropertyNames = { "Version", "PackageId" };
        private static readonly string[] ItemTypes = { "PackageReference" };

        private readonly string _directory = Path.Combine(Path.GetTempPath(), "cyclonedx-evaluation-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, true);
            }
            catch (IOException)
            {
                // best effort cleanup of the temporary directory
            }
        }

        // -----------------------------------------------------------------------
        // Mocked dotnet: the .NET SDK whose MSBuild evaluates the projects
        // -----------------------------------------------------------------------

        private static readonly string Sdks = XFS.Path(@"c:\dotnet\sdk");

        private static Mock<IDotnetCommandService> MockDotnet(DotnetCommandResult version, string listSdks = "")
        {
            var dotnet = new Mock<IDotnetCommandService>(MockBehavior.Strict);
            dotnet.Setup(m => m.Run("--version")).Returns(version);
            dotnet.Setup(m => m.Run("--list-sdks")).Returns(new DotnetCommandResult { ExitCode = 0, StdOut = listSdks });
            return dotnet;
        }

        [Fact]
        public void GetSdkDirectory_IsTheSdkTheDotnetCliSelects()
        {
            // e.g. global.json selects 8.0.400, although 10.0.100 is installed as well
            var dotnet = MockDotnet(
                new DotnetCommandResult { ExitCode = 0, StdOut = "8.0.400" + Environment.NewLine },
                string.Join(Environment.NewLine, $"8.0.100 [{Sdks}]", $"8.0.400 [{Sdks}]", $"10.0.100 [{Sdks}]", ""));

            var sdkDirectory = new ProjectEvaluationService(new MockFileSystem(), dotnet.Object).GetSdkDirectory();

            Assert.True(sdkDirectory.Success, sdkDirectory.ErrorMessage);
            Assert.Equal(XFS.Path(@"c:\dotnet\sdk\8.0.400"), sdkDirectory.Result);
        }

        [Fact]
        public void EvaluateProjects_SdkNeedsNewerRuntime_EveryProjectGetsAnError()
        {
            // e.g. CycloneDX runs on .NET 8, but the dotnet CLI uses the .NET 10 SDK, whose MSBuild needs .NET 10
            var sdkVersion = $"{Environment.Version.Major + 1}.0.100";
            var dotnet = MockDotnet(new DotnetCommandResult { ExitCode = 0, StdOut = sdkVersion }, $"{sdkVersion} [{Sdks}]");
            var projects = new[] { XFS.Path(@"c:\P1\P1.csproj"), XFS.Path(@"c:\P2\P2.csproj") };

            var results = new ProjectEvaluationService(new MockFileSystem(), dotnet.Object)
                .EvaluateProjects(projects, PropertyNames, ItemTypes, null, null);

            Assert.All(projects, project => Assert.Equal(
                $"The MSBuild of the .NET SDK {sdkVersion} cannot run on .NET {Environment.Version}, which runs CycloneDX",
                results[project].ErrorMessage));
        }

        [Fact]
        public void EvaluateProjects_DotnetCliFindsNoSdk_EveryProjectGetsTheErrorOnOneLine()
        {
            // e.g. global.json requests an SDK that is not installed
            var dotnet = MockDotnet(new DotnetCommandResult
            {
                ExitCode = 155,
                StdOut = $"10.0.100 [{Sdks}]",
                StdErr = string.Join(Environment.NewLine, "A compatible .NET SDK was not found.", "", "Requested SDK version: 99.0.100", "")
            });
            var projects = new[] { XFS.Path(@"c:\P1\P1.csproj"), XFS.Path(@"c:\P2\P2.csproj") };

            var results = new ProjectEvaluationService(new MockFileSystem(), dotnet.Object)
                .EvaluateProjects(projects, PropertyNames, ItemTypes, null, null);

            Assert.All(projects, project => Assert.Equal(
                "Could not determine the .NET SDK: A compatible .NET SDK was not found. Requested SDK version: 99.0.100",
                results[project].ErrorMessage));
        }

        // -----------------------------------------------------------------------
        // Real dotnet: projects in a temporary directory
        // -----------------------------------------------------------------------

        // The Directory.Build.props also stops MSBuild from picking one up from a parent directory
        private string WriteProject(string name, string csproj, string directoryBuildProps = "<Project />")
        {
            var projectDirectory = Path.Combine(_directory, name);
            Directory.CreateDirectory(projectDirectory);
            File.WriteAllText(Path.Combine(_directory, "Directory.Build.props"), directoryBuildProps);
            var projectFilePath = Path.Combine(projectDirectory, name + ".csproj");
            File.WriteAllText(projectFilePath, csproj);
            return projectFilePath;
        }

        private static Dictionary<string, DotnetUtilsResult<EvaluatedProject>> Evaluate(IEnumerable<string> projects, string framework = null, string configuration = null)
            => new ProjectEvaluationService(new FileSystem(), new DotnetCommandService())
                .EvaluateProjects(projects, new[] { "Version", "PackageId", "IsTestProject" }, ItemTypes, framework, configuration);

        [Fact]
        public void EvaluateProjects_Dotnet_SeesValuesAndItemsFromDirectoryBuildProps()
        {
            var project = WriteProject(
                "App.Tests",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>",
                "<Project><PropertyGroup><Version>4.5.6</Version><AssemblyName>Contoso.$(MSBuildProjectName)</AssemblyName></PropertyGroup>" +
                "<ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.11.1\" /></ItemGroup></Project>");

            var result = Evaluate(new[] { project })[project];

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal("4.5.6", result.Result.Properties["Version"]);
            Assert.Equal("Contoso.App.Tests", result.Result.Properties["PackageId"]);
            Assert.Contains("Microsoft.NET.Test.Sdk", result.Result.Items["PackageReference"]);
        }

        [Theory]
        [InlineData(null, null, "1.0.0")]
        [InlineData(null, "Release", "2.0.0")]
        [InlineData("net9.0", null, "9.0.0")]
        public void EvaluateProjects_Dotnet_AppliesFrameworkAndConfiguration(string framework, string configuration, string expectedVersion)
        {
            var project = WriteProject(
                "App",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>net8.0;net9.0</TargetFrameworks></PropertyGroup></Project>",
                "<Project>" +
                "<PropertyGroup Condition=\"'$(Configuration)' == 'Release'\"><Version>2.0.0</Version></PropertyGroup>" +
                "<PropertyGroup Condition=\"'$(TargetFramework)' == 'net9.0'\"><Version>9.0.0</Version></PropertyGroup>" +
                "</Project>");

            var result = Evaluate(new[] { project }, framework, configuration)[project];

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(expectedVersion, result.Result.Properties["Version"]);
        }

        [Fact]
        public void EvaluateProjects_Dotnet_ProjectPathWithSpecialCharacters()
        {
            var project = WriteProject("100% R&D;$(Odd)'s @Lib", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework><Version>7.8.9</Version></PropertyGroup></Project>");

            var result = Evaluate(new[] { project })[project];

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal("7.8.9", result.Result.Properties["Version"]);
        }

        [Fact]
        public void EvaluateProjects_Dotnet_BrokenProjectDoesNotPreventTheOthers()
        {
            var good = WriteProject("Good", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework><Version>3.2.1</Version></PropertyGroup></Project>");
            var broken = WriteProject("Broken", "<Project Sdk=\"Microsoft.NET.Sdk\"><Import Project=\"missing.props\" /></Project>");
            Directory.CreateDirectory(Path.Combine(_directory, "NoProject"));
            var notXml = WriteProject("NotXml", "this is not a project file");
            var paths = new[] { good, broken, Path.Combine(_directory, "NoProject"), notXml, Path.Combine(_directory, "Missing", "Missing.csproj") };

            var results = Evaluate(paths);

            Assert.True(results[good].Success, results[good].ErrorMessage);
            Assert.Equal("3.2.1", results[good].Result.Properties["Version"]);
            Assert.Contains("MSB4019", results[broken].ErrorMessage, StringComparison.Ordinal);
            Assert.StartsWith("error MSB3202", results[paths[4]].ErrorMessage, StringComparison.Ordinal);
            Assert.All(paths.Skip(1), path => Assert.False(results[path].Success, path));
        }

        [Fact]
        public void EvaluateProjects_Dotnet_DoesNotChangeTheEnvironment()
        {
            // The dotnet processes started later, e.g. for dotnet restore, inherit it and must use their own SDK
            var project = WriteProject("App", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
            var environment = GetEnvironment();

            var result = Evaluate(new[] { project })[project];

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(environment, GetEnvironment());
        }

        private static SortedDictionary<string, string> GetEnvironment()
            => new SortedDictionary<string, string>(
                Environment.GetEnvironmentVariables().Cast<DictionaryEntry>().ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value),
                StringComparer.Ordinal);
    }
}
