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
using System.Collections.Generic;
using System.IO;
using CycloneDX.Models;
using CycloneDX.Services;
using Xunit;

namespace CycloneDX.Tests
{
    /// <summary>
    /// Runs the MSBuild of the installed .NET SDK on real project files in a temporary directory.
    /// </summary>
    public sealed class ProjectEvaluationServiceTests : IDisposable
    {
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

        // The Directory.Build.props also stops MSBuild from picking one up from a parent directory
        private string WriteProject(string directoryBuildProps, string csproj)
        {
            var projectDirectory = Path.Combine(_directory, "App");
            Directory.CreateDirectory(projectDirectory);
            File.WriteAllText(Path.Combine(_directory, "Directory.Build.props"), directoryBuildProps);
            var projectFilePath = Path.Combine(projectDirectory, "App.csproj");
            File.WriteAllText(projectFilePath, csproj);
            return projectFilePath;
        }

        // The dotnet CLI may resolve an SDK that needs a newer runtime than this test run (e.g. SDK 10
        // on net8.0). Evaluating in-process is then not possible by design and callers read the project file.
        private static bool MSBuildCannotBeLoaded(DotnetUtilsResult<Dictionary<string, string>> result)
        {
            if (ProjectEvaluationService.GetMSBuildUnavailableReason() is null)
            {
                return false;
            }
            Assert.False(result.Success);
            return true;
        }

        [Fact]
        public void GetProjectProperties_SeesValuesFromDirectoryBuildProps()
        {
            var projectFilePath = WriteProject(
                "<Project><PropertyGroup><Version>4.5.6</Version><AssemblyName>Contoso.$(MSBuildProjectName)</AssemblyName></PropertyGroup></Project>",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");

            var result = new ProjectEvaluationService().GetProjectProperties(projectFilePath, new[] { "Version", "PackageId" }, null, null);
            if (MSBuildCannotBeLoaded(result))
            {
                return;
            }

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal("4.5.6", result.Result["Version"]);
            Assert.Equal("Contoso.App", result.Result["PackageId"]);
        }

        [Theory]
        [InlineData(null, null, "1.0.0")]
        [InlineData(null, "Release", "2.0.0")]
        [InlineData("net9.0", null, "9.0.0")]
        public void GetProjectProperties_AppliesFrameworkAndConfiguration(string framework, string configuration, string expectedVersion)
        {
            var projectFilePath = WriteProject(
                "<Project>" +
                "<PropertyGroup Condition=\"'$(Configuration)' == 'Release'\"><Version>2.0.0</Version></PropertyGroup>" +
                "<PropertyGroup Condition=\"'$(TargetFramework)' == 'net9.0'\"><Version>9.0.0</Version></PropertyGroup>" +
                "</Project>",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>net8.0;net9.0</TargetFrameworks></PropertyGroup></Project>");

            var result = new ProjectEvaluationService().GetProjectProperties(projectFilePath, new[] { "Version" }, framework, configuration);
            if (MSBuildCannotBeLoaded(result))
            {
                return;
            }

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(expectedVersion, result.Result["Version"]);
        }

        [Fact]
        public void GetProjectProperties_MissingImport_ReturnsTheMsbuildError()
        {
            var projectFilePath = WriteProject(
                "<Project />",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><Import Project=\"missing.props\" /><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");

            var result = new ProjectEvaluationService().GetProjectProperties(projectFilePath, new[] { "Version" }, null, null);
            if (MSBuildCannotBeLoaded(result))
            {
                return;
            }

            Assert.False(result.Success);
            Assert.Contains("MSB4019", result.ErrorMessage);
        }
    }
}
