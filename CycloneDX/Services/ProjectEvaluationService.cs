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
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using CycloneDX.Interfaces;
using CycloneDX.Models;

namespace CycloneDX.Services
{
    /// <summary>
    /// Evaluates projects in this process with the MSBuild of the .NET SDK that the dotnet CLI uses. MSBuild and
    /// the other assemblies of the SDK are loaded into a load context of their own, so that they get the versions
    /// they were built with instead of those CycloneDX ships, e.g. of NuGet.Frameworks.
    /// </summary>
    public sealed class ProjectEvaluationService : IProjectEvaluationService
    {
        // Loaded by name, so that its MSBuild types never load into the default load context
        private const string EvaluatorTypeName = "CycloneDX.Services.MSBuildEvaluator";

        // MSBuild cannot be unloaded, so it is loaded once per SDK directory and process
        private static readonly Dictionary<string, Evaluator> Evaluators = new Dictionary<string, Evaluator>(StringComparer.Ordinal);

        private readonly IFileSystem _fileSystem;
        private readonly IDotnetCommandService _dotnetCommandService;
        private DotnetUtilsResult<string> _sdkDirectory;

        // The signature of MSBuildEvaluator.Evaluate; only BCL types cross the load context boundary
        private delegate List<(Dictionary<string, string> Properties, Dictionary<string, string[]> Items, string Error)> Evaluator(
            string[] projectFilePaths, string[] propertyNames, string[] itemTypes, Dictionary<string, string> globalProperties);

        public ProjectEvaluationService(IFileSystem fileSystem, IDotnetCommandService dotnetCommandService)
        {
            _fileSystem = fileSystem;
            _dotnetCommandService = dotnetCommandService;
        }

        public Dictionary<string, DotnetUtilsResult<EvaluatedProject>> EvaluateProjects(
            IEnumerable<string> projectFilePaths, IEnumerable<string> propertyNames, IEnumerable<string> itemTypes,
            string framework, string configuration)
        {
            var projects = projectFilePaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (projects.Count == 0)
            {
                return new Dictionary<string, DotnetUtilsResult<EvaluatedProject>>(StringComparer.OrdinalIgnoreCase);
            }

            _sdkDirectory ??= GetSdkDirectory();
            if (!_sdkDirectory.Success)
            {
                return Failed(projects, _sdkDirectory.ErrorMessage);
            }

            // Passed like to `dotnet restore`
            var globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(framework))
            {
                globalProperties["TargetFramework"] = framework;
            }
            if (!string.IsNullOrEmpty(configuration))
            {
                globalProperties["Configuration"] = configuration;
            }

            List<(Dictionary<string, string> Properties, Dictionary<string, string[]> Items, string Error)> evaluations;
            try
            {
                evaluations = GetEvaluator(_sdkDirectory.Result)(
                    projects.Select(_fileSystem.Path.GetFullPath).ToArray(), propertyNames.ToArray(), itemTypes.ToArray(), globalProperties);
            }
            catch (Exception exception)
            {
                // e.g. MSBuild cannot be loaded; the project files are read instead
                return Failed(projects, OneLine($"MSBuild of the .NET SDK in {_sdkDirectory.Result} failed: {exception.Message}"));
            }

            return projects.Zip(evaluations).ToDictionary(
                evaluation => evaluation.First,
                evaluation => evaluation.Second.Error is null
                    ? new DotnetUtilsResult<EvaluatedProject> { Result = ToEvaluatedProject(evaluation.Second.Properties, evaluation.Second.Items) }
                    : new DotnetUtilsResult<EvaluatedProject> { ErrorMessage = OneLine(evaluation.Second.Error) },
                StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The directory of the .NET SDK that the dotnet CLI uses in the current directory, as selected by global.json,
        /// like for `dotnet restore`. Fails when this .NET runtime cannot run the MSBuild of the SDK.
        /// </summary>
        internal DotnetUtilsResult<string> GetSdkDirectory()
        {
            var version = _dotnetCommandService.Run("--version");
            var sdks = version.Success ? _dotnetCommandService.Run("--list-sdks") : version;
            if (!sdks.Success)
            {
                var message = string.IsNullOrWhiteSpace(sdks.StdErr) ? sdks.StdOut : sdks.StdErr;
                return new DotnetUtilsResult<string> { ErrorMessage = OneLine($"Could not determine the .NET SDK: {message}") };
            }

            var sdkVersion = version.StdOut.Trim();
            // e.g. "10.0.100 [C:\Program Files\dotnet\sdk]"
            var sdkDirectory = sdks.StdOut
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.StartsWith(sdkVersion + " [", StringComparison.Ordinal) && line.EndsWith(']'))
                .Select(line => _fileSystem.Path.Combine(line.Substring(sdkVersion.Length + 2, line.Length - sdkVersion.Length - 3), sdkVersion))
                .FirstOrDefault();
            if (sdkDirectory is null)
            {
                return new DotnetUtilsResult<string> { ErrorMessage = $"Could not find the .NET SDK {sdkVersion}" };
            }

            // The MSBuild of an SDK targets the .NET of the same major version
            if (!int.TryParse(sdkVersion.Split('.')[0], out var sdkMajorVersion) || sdkMajorVersion > Environment.Version.Major)
            {
                return new DotnetUtilsResult<string>
                {
                    ErrorMessage = $"The MSBuild of the .NET SDK {sdkVersion} cannot run on .NET {Environment.Version}, which runs CycloneDX"
                };
            }
            return new DotnetUtilsResult<string> { Result = sdkDirectory };
        }

        private static Evaluator GetEvaluator(string sdkDirectory)
        {
            lock (Evaluators)
            {
                if (!Evaluators.TryGetValue(sdkDirectory, out var evaluator))
                {
                    // A second instance of this assembly, whose references resolve to the SDK
                    evaluator = new SdkLoadContext(sdkDirectory)
                        .LoadFromAssemblyPath(typeof(ProjectEvaluationService).Assembly.Location)
                        .GetType(EvaluatorTypeName, throwOnError: true)
                        .GetMethod("Evaluate")
                        .CreateDelegate<Evaluator>();
                    Evaluators[sdkDirectory] = evaluator;
                }
                return evaluator;
            }
        }

        private static EvaluatedProject ToEvaluatedProject(Dictionary<string, string> properties, Dictionary<string, string[]> items)
        {
            var evaluatedProject = new EvaluatedProject();
            foreach (var property in properties)
            {
                evaluatedProject.Properties[property.Key] = property.Value;
            }
            foreach (var item in items)
            {
                evaluatedProject.Items[item.Key] = item.Value;
            }
            return evaluatedProject;
        }

        private static Dictionary<string, DotnetUtilsResult<EvaluatedProject>> Failed(IEnumerable<string> projects, string errorMessage)
        {
            return projects.ToDictionary(
                project => project,
                _ => new DotnetUtilsResult<EvaluatedProject> { ErrorMessage = errorMessage },
                StringComparer.OrdinalIgnoreCase);
        }

        private static string OneLine(string message)
        {
            return string.Join(" ", message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()));
        }

        /// <summary>
        /// Loads the assemblies that the .NET SDK ships from its directory, except those that are part of the .NET
        /// runtime, which the SDK also uses from the runtime. Everything else comes from the default load context.
        /// </summary>
        private sealed class SdkLoadContext : AssemblyLoadContext
        {
            private readonly string _sdkDirectory;

            public SdkLoadContext(string sdkDirectory) : base($"MSBuild of {sdkDirectory}")
            {
                _sdkDirectory = sdkDirectory;
                // MSBuild loads SDK resolvers into load contexts that leave the MSBuild assemblies to the default context
                AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
                {
                    var name = new AssemblyName(args.Name);
                    return (name.Name == "MSBuild" || name.Name.StartsWith("Microsoft.Build", StringComparison.Ordinal))
                        && GetSdkAssemblyPath(name) != null
                        ? LoadFromAssemblyName(name)
                        : null;
                };
            }

            protected override Assembly Load(AssemblyName assemblyName)
            {
                var path = GetSdkAssemblyPath(assemblyName);
                return path is null ? null : LoadFromAssemblyPath(path);
            }

            private string GetSdkAssemblyPath(AssemblyName assemblyName)
            {
                var fileName = assemblyName.Name + ".dll";
                var path = Path.Combine(_sdkDirectory, assemblyName.CultureName ?? string.Empty, fileName);
                return File.Exists(path) && !File.Exists(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), fileName))
                    ? path
                    : null;
            }
        }
    }
}
