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
using System.Linq;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Exceptions;

namespace CycloneDX.Services
{
    /// <summary>
    /// Evaluates projects with the MSBuild API. Runs only in the load context of the .NET SDK, see
    /// <see cref="ProjectEvaluationService"/>, which calls it by name with BCL types only.
    /// </summary>
    internal static class MSBuildEvaluator
    {
        /// <returns>For each project its properties and items, or the error.</returns>
        public static List<(Dictionary<string, string> Properties, Dictionary<string, string[]> Items, string Error)> Evaluate(
            string[] projectFilePaths, string[] propertyNames, string[] itemTypes, Dictionary<string, string> globalProperties)
        {
            // Imports such as the SDK targets are parsed once for all projects, and like in a build,
            // file system lookups and SDK resolution are cached
            using var projectCollection = new ProjectCollection(globalProperties);
            var options = new ProjectOptions
            {
                ProjectCollection = projectCollection,
                EvaluationContext = EvaluationContext.Create(EvaluationContext.SharingPolicy.Shared)
            };
            return projectFilePaths
                .Select(projectFilePath => Evaluate(options, projectFilePath, propertyNames, itemTypes))
                .ToList();
        }

        private static (Dictionary<string, string> Properties, Dictionary<string, string[]> Items, string Error) Evaluate(
            ProjectOptions options, string projectFilePath, string[] propertyNames, string[] itemTypes)
        {
            if (!File.Exists(projectFilePath))
            {
                // The error `dotnet msbuild` reports for it
                return (null, null, $"error MSB3202: The project file \"{projectFilePath}\" was not found.");
            }
            try
            {
                var project = Project.FromFile(projectFilePath, options);
                var properties = propertyNames.ToDictionary(name => name, project.GetPropertyValue, StringComparer.OrdinalIgnoreCase);
                var items = itemTypes.ToDictionary(
                    itemType => itemType,
                    itemType => project.GetItems(itemType).Select(item => item.EvaluatedInclude).ToArray(),
                    StringComparer.OrdinalIgnoreCase);
                options.ProjectCollection.UnloadProject(project);
                return (properties, items, null);
            }
            catch (InvalidProjectFileException exception)
            {
                // In the format of `dotnet msbuild`, e.g. "App.csproj(2,3): error MSB4019: ..."
                return (null, null, $"{exception.ProjectFile}({exception.LineNumber},{exception.ColumnNumber}): error {exception.ErrorCode}: {exception.BaseMessage}");
            }
        }
    }
}
