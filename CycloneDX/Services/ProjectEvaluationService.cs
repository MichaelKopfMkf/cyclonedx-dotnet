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
using System.Linq;
using System.Runtime.CompilerServices;
using CycloneDX.Interfaces;
using CycloneDX.Models;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Locator;

namespace CycloneDX.Services
{
    /// <summary>
    /// Evaluates project properties in-process with the MSBuild of the .NET SDK that the dotnet CLI
    /// resolves (honoring global.json). That is much faster than starting `dotnet msbuild` per project.
    /// </summary>
    public sealed class ProjectEvaluationService : IProjectEvaluationService
    {
        private static readonly object RegistrationLock = new object();
        private static bool _registrationAttempted;
        private static string _unavailableReason;

        // Typed as object: the Microsoft.Build assemblies must not load before MSBuildLocator registered them
        private object _evaluator;

        /// <summary>
        /// Registers the MSBuild of the SDK resolved by the dotnet CLI once per process.
        /// Returns why MSBuild cannot be used, or null when it is registered.
        /// </summary>
        internal static string GetMSBuildUnavailableReason()
        {
            lock (RegistrationLock)
            {
                if (!_registrationAttempted)
                {
                    _registrationAttempted = true;
                    _unavailableReason = MSBuildLocator.IsRegistered ? null : RegisterResolvedSdk();
                }
                return _unavailableReason;
            }
        }

        private static string RegisterResolvedSdk()
        {
            // Without this the locator silently picks an older SDK matching the runtime and points
            // MSBuildSDKsPath of this process (and the `dotnet restore` it starts) to it.
            MSBuildLocator.AllowQueryAllRuntimeVersions = true;
            var resolvedSdk = MSBuildLocator.QueryVisualStudioInstances().FirstOrDefault();
            if (resolvedSdk is null)
            {
                return "No .NET SDK found";
            }
            if (resolvedSdk.Version.Major > Environment.Version.Major)
            {
                return $"The .NET SDK {resolvedSdk.Version} resolved by the dotnet CLI cannot be loaded by the .NET {Environment.Version.Major} runtime running CycloneDX";
            }
            MSBuildLocator.RegisterInstance(resolvedSdk);
            return null;
        }

        public DotnetUtilsResult<Dictionary<string, string>> GetProjectProperties(string projectFilePath, IEnumerable<string> propertyNames, string framework, string configuration)
        {
            var unavailableReason = GetMSBuildUnavailableReason();
            if (unavailableReason != null)
            {
                return new DotnetUtilsResult<Dictionary<string, string>> { ErrorMessage = unavailableReason };
            }
            return Evaluate(projectFilePath, propertyNames, framework, configuration);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private DotnetUtilsResult<Dictionary<string, string>> Evaluate(string projectFilePath, IEnumerable<string> propertyNames, string framework, string configuration)
        {
            _evaluator ??= new MSBuildEvaluator();
            return ((MSBuildEvaluator)_evaluator).Evaluate(projectFilePath, propertyNames, framework, configuration);
        }

        /// <summary>Uses Microsoft.Build types, so it is only created after MSBuildLocator registered the SDK.</summary>
        private sealed class MSBuildEvaluator
        {
            // One collection per set of global properties; MSBuild caches imports such as Directory.Build.props
            private readonly Dictionary<string, ProjectCollection> _projectCollections = new Dictionary<string, ProjectCollection>(StringComparer.Ordinal);

            public DotnetUtilsResult<Dictionary<string, string>> Evaluate(string projectFilePath, IEnumerable<string> propertyNames, string framework, string configuration)
            {
                var projectCollection = GetProjectCollection(framework, configuration);
                try
                {
                    var project = projectCollection.LoadProject(projectFilePath);
                    try
                    {
                        var properties = propertyNames.ToDictionary(name => name, name => project.GetPropertyValue(name), StringComparer.OrdinalIgnoreCase);
                        return new DotnetUtilsResult<Dictionary<string, string>> { Result = properties };
                    }
                    finally
                    {
                        projectCollection.UnloadProject(project);
                    }
                }
                catch (InvalidProjectFileException ex)
                {
                    // Same format as `dotnet msbuild` reports it, e.g. "App.csproj(2,3): error MSB4019: ..."
                    return new DotnetUtilsResult<Dictionary<string, string>>
                    {
                        ErrorMessage = $"{ex.ProjectFile}({ex.LineNumber},{ex.ColumnNumber}): error {ex.ErrorCode}: {ex.BaseMessage}"
                    };
                }
            }

            private ProjectCollection GetProjectCollection(string framework, string configuration)
            {
                var key = $"{framework}|{configuration}";
                if (!_projectCollections.TryGetValue(key, out var projectCollection))
                {
                    // Passed like to `dotnet restore`, so the evaluation sees the same global properties
                    var globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (!string.IsNullOrEmpty(framework))
                    {
                        globalProperties["TargetFramework"] = framework;
                    }
                    if (!string.IsNullOrEmpty(configuration))
                    {
                        globalProperties["Configuration"] = configuration;
                    }
                    projectCollection = new ProjectCollection(globalProperties);
                    _projectCollections[key] = projectCollection;
                }
                return projectCollection;
            }
        }
    }
}
