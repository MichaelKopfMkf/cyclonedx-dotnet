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
using System.IO.Abstractions.TestingHelpers;
using CycloneDX.Interfaces;
using CycloneDX.Models;
using CycloneDX.Services;
using Moq;
using Xunit;
using XFS = System.IO.Abstractions.TestingHelpers.MockUnixSupport;

namespace CycloneDX.Tests
{
    // Redirects Console.Out, so it must not run in parallel with tests that write to the console
    [Collection("Non-Parallel")]
    public class ProjectEvaluationNoticeTests
    {
        private const string Notice = "Could not evaluate properties of Notice.csproj with MSBuild, reading the project file instead";
        private static readonly string ProjectPath = XFS.Path(@"c:\Notice\Notice.csproj");

        private static ProjectFileService CreateProjectFileService(DotnetUtilsResult<Dictionary<string, string>> evaluationResult)
        {
            var mockFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
            {
                { ProjectPath, new MockFileData("<Project><PropertyGroup><Version>1.2.3</Version></PropertyGroup></Project>") }
            });
            var dotnetUtils = new Mock<IDotnetUtilsService>();
            dotnetUtils
                .Setup(m => m.GetProjectProperties(ProjectPath, It.IsAny<IEnumerable<string>>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns(evaluationResult);
            return new ProjectFileService(
                mockFileSystem,
                dotnetUtils.Object,
                new Mock<IPackagesFileService>().Object,
                new Mock<IProjectAssetsFileService>().Object);
        }

        private static string CaptureOutput(Action action)
        {
            var originalOut = Console.Out;
            using var capturedOut = new StringWriter();
            Console.SetOut(capturedOut);
            try
            {
                action();
            }
            finally
            {
                Console.SetOut(originalOut);
            }
            return capturedOut.ToString();
        }

        [Fact]
        public void EvaluationFails_WritesOneNoticePerProjectWithTheMsbuildError()
        {
            var svc = CreateProjectFileService(new DotnetUtilsResult<Dictionary<string, string>>
            {
                ErrorMessage = "  Determining projects to restore...\r\n"
                    + XFS.Path(@"c:\Notice\Notice.csproj") + "(2,3): error MSB4019: The imported project \"common.props\" was not found.\r\n"
            });

            var output = CaptureOutput(() =>
            {
                svc.IsTestProject(ProjectPath);
                svc.GetAssemblyNameAndVersion(ProjectPath);
                svc.GetAssemblyNameAndVersion(ProjectPath);
            });

            var notice = Assert.Single(output.Split('\n'), line => line.Contains(Notice));
            Assert.Contains("error MSB4019", notice);
        }

        [Fact]
        public void EvaluationSucceeds_WritesNoNotice()
        {
            var svc = CreateProjectFileService(new DotnetUtilsResult<Dictionary<string, string>>
            {
                Result = new Dictionary<string, string> { ["Version"] = "2.0.0", ["PackageId"] = "Notice" }
            });

            var output = CaptureOutput(() => svc.GetAssemblyNameAndVersion(ProjectPath));

            Assert.DoesNotContain("Could not evaluate", output);
        }
    }
}
