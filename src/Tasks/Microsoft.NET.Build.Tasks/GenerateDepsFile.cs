﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.Extensions.DependencyModel;
using Newtonsoft.Json;
using NuGet.Packaging.Core;
using NuGet.ProjectModel;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Microsoft.NET.Build.Tasks
{
    /// <summary>
    /// Generates the $(project).deps.json file.
    /// </summary>
    public class GenerateDepsFile : TaskBase
    {
        [Required]
        public string ProjectPath { get; set; }

        [Required]
        public string AssetsFilePath { get; set; }

        [Required]
        public string DepsFilePath { get; set; }

        [Required]
        public string TargetFramework { get; set; }

        public string RuntimeIdentifier { get; set; }

        public string PlatformLibraryName { get; set; }

        [Required]
        public string AssemblyName { get; set; }

        [Required]
        public string AssemblyExtension { get; set; }

        [Required]
        public string AssemblyVersion { get; set; }

        [Required]
        public ITaskItem[] AssemblySatelliteAssemblies { get; set; }

        [Required]
        public ITaskItem[] ReferencePaths { get; set; }

        [Required]
        public ITaskItem[] ReferenceSatellitePaths { get; set; }

        [Required]
        public ITaskItem[] FilesToSkip { get; set; }

        public ITaskItem CompilerOptions { get; set; }

        public ITaskItem[] PrivateAssetsPackageReferences { get; set; }

        public string[] TargetManifestFileList { get; set; }

        public bool IsSelfContained { get; set; }

        List<ITaskItem> _filesWritten = new List<ITaskItem>();

        [Output]
        public ITaskItem[] FilesWritten
        {
            get { return _filesWritten.ToArray(); }
        }

        private Dictionary<string, HashSet<string>> compileFilesToSkip = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, HashSet<string>> runtimeFilesToSkip = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        private Dictionary<PackageIdentity, StringBuilder> GetFilteredPackages()
        {
            Dictionary<PackageIdentity, StringBuilder> filteredPackages = null;

            if (TargetManifestFileList != null && TargetManifestFileList.Length > 0)
            {
                filteredPackages = new Dictionary<PackageIdentity, StringBuilder>();

                foreach (var targetManifestFile in TargetManifestFileList)
                {
                    Log.LogMessage(MessageImportance.Low, string.Format(CultureInfo.CurrentCulture, Strings.ParsingFiles, targetManifestFile));
                    var packagesSpecified = StoreArtifactParser.Parse(targetManifestFile);
                    var targetManifestFileName = Path.GetFileName(targetManifestFile);

                    foreach (var pkg in packagesSpecified)
                    {
                        Log.LogMessage(MessageImportance.Low, string.Format(CultureInfo.CurrentCulture, Strings.PackageInfoLog, pkg.Id, pkg.Version));
                        StringBuilder fileList;
                        if (filteredPackages.TryGetValue(pkg, out fileList))
                        {
                            fileList.Append($";{targetManifestFileName}");
                        }
                        else
                        {
                            filteredPackages.Add(pkg, new StringBuilder(targetManifestFileName));
                        }
                    }
                }
            }
            return filteredPackages;
        }

        protected override void ExecuteCore()
        {
            LoadFilesToSkip();

            LockFile lockFile = new LockFileCache(BuildEngine4).GetLockFile(AssetsFilePath);
            CompilationOptions compilationOptions = CompilationOptionsConverter.ConvertFrom(CompilerOptions);

            SingleProjectInfo mainProject = SingleProjectInfo.Create(
                ProjectPath,
                AssemblyName,
                AssemblyExtension,
                AssemblyVersion,
                AssemblySatelliteAssemblies);

            IEnumerable<ReferenceInfo> frameworkReferences =
                ReferenceInfo.CreateFrameworkReferenceInfos(ReferencePaths);

            IEnumerable<ReferenceInfo> directReferences =
                ReferenceInfo.CreateDirectReferenceInfos(ReferencePaths, ReferenceSatellitePaths);

            Dictionary<string, SingleProjectInfo> referenceProjects = SingleProjectInfo.CreateProjectReferenceInfos(
                ReferencePaths,
                ReferenceSatellitePaths);

            IEnumerable<string> privateAssets = PackageReferenceConverter.GetPackageIds(PrivateAssetsPackageReferences);

            ProjectContext projectContext = lockFile.CreateProjectContext(
                NuGetUtils.ParseFrameworkName(TargetFramework),
                RuntimeIdentifier,
                PlatformLibraryName,
                IsSelfContained);

            DependencyContext dependencyContext = new DependencyContextBuilder(mainProject, projectContext)
                .WithFrameworkReferences(frameworkReferences)
                .WithDirectReferences(directReferences)
                .WithReferenceProjectInfos(referenceProjects)
                .WithPrivateAssets(privateAssets)
                .WithCompilationOptions(compilationOptions)
                .WithReferenceAssembliesPath(FrameworkReferenceResolver.GetDefaultReferenceAssembliesPath())
                .WithPackagesThatWhereFiltered(GetFilteredPackages())
                .Build();

            if (compileFilesToSkip.Any() || runtimeFilesToSkip.Any())
            {
                dependencyContext = TrimFilesToSkip(dependencyContext);
            }

            var writer = new DependencyContextWriter();
            using (var fileStream = File.Create(DepsFilePath))
            {
                writer.Write(dependencyContext, fileStream);
            }
            _filesWritten.Add(new TaskItem(DepsFilePath));

        }

        private void LoadFilesToSkip()
        {
            foreach (var fileToSkip in FilesToSkip)
            {
                string packageId, packageSubPath;
                NuGetUtils.GetPackageParts(fileToSkip.ItemSpec, out packageId, out packageSubPath);

                if (String.IsNullOrEmpty(packageId) || String.IsNullOrEmpty(packageSubPath))
                {
                    continue;
                }

                var itemType = fileToSkip.GetMetadata(nameof(ConflictResolution.ConflictItemType));
                var packagesWithFilesToSkip = (itemType == nameof(ConflictResolution.ConflictItemType.Reference)) ? compileFilesToSkip : runtimeFilesToSkip;

                HashSet<string> filesToSkipForPackage;
                if (!packagesWithFilesToSkip.TryGetValue(packageId, out filesToSkipForPackage))
                {
                    packagesWithFilesToSkip[packageId] = filesToSkipForPackage = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                filesToSkipForPackage.Add(packageSubPath);
            }
        }

        private DependencyContext TrimFilesToSkip(DependencyContext sourceDeps)
        {
            return new DependencyContext(sourceDeps.Target,
                                         sourceDeps.CompilationOptions,
                                         TrimCompilationLibraries(sourceDeps.CompileLibraries),
                                         TrimRuntimeLibraries(sourceDeps.RuntimeLibraries),
                                         sourceDeps.RuntimeGraph);
        }

        private IEnumerable<RuntimeLibrary> TrimRuntimeLibraries(IReadOnlyList<RuntimeLibrary> runtimeLibraries)
        {
            foreach (var runtimeLibrary in runtimeLibraries)
            {
                HashSet<string> filesToSkip;
                if (runtimeFilesToSkip.TryGetValue(runtimeLibrary.Name, out filesToSkip))
                {
                    yield return new RuntimeLibrary(runtimeLibrary.Type,
                                              runtimeLibrary.Name,
                                              runtimeLibrary.Version,
                                              runtimeLibrary.Hash,
                                              TrimAssetGroups(runtimeLibrary.RuntimeAssemblyGroups, filesToSkip).ToArray(),
                                              TrimAssetGroups(runtimeLibrary.NativeLibraryGroups, filesToSkip).ToArray(),
                                              TrimResourceAssemblies(runtimeLibrary.ResourceAssemblies, filesToSkip),
                                              runtimeLibrary.Dependencies,
                                              runtimeLibrary.Serviceable,
                                              runtimeLibrary.Path,
                                              runtimeLibrary.HashPath,
                                              runtimeLibrary.RuntimeStoreManifestName);
                }
                else
                {
                    yield return runtimeLibrary;
                }
            }
        }

        private IEnumerable<RuntimeAssetGroup> TrimAssetGroups(IEnumerable<RuntimeAssetGroup> assetGroups, ISet<string> filesToTrim)
        {
            foreach (var assetGroup in assetGroups)
            {
                yield return new RuntimeAssetGroup(assetGroup.Runtime, TrimAssemblies(assetGroup.AssetPaths, filesToTrim));
            }
        }

        private IEnumerable<ResourceAssembly> TrimResourceAssemblies(IEnumerable<ResourceAssembly> resourceAssemblies, ISet<string> filesToTrim)
        {
            foreach (var resourceAssembly in resourceAssemblies)
            {
                if (!filesToTrim.Contains(resourceAssembly.Path))
                {
                    yield return resourceAssembly;
                }
            }
        }

        private IEnumerable<CompilationLibrary> TrimCompilationLibraries(IReadOnlyList<CompilationLibrary> compileLibraries)
        {
            foreach (var compileLibrary in compileLibraries)
            {
                HashSet<string> filesToSkip;
                if (compileFilesToSkip.TryGetValue(compileLibrary.Name, out filesToSkip))
                {
                    yield return new CompilationLibrary(compileLibrary.Type,
                                              compileLibrary.Name,
                                              compileLibrary.Version,
                                              compileLibrary.Hash,
                                              TrimAssemblies(compileLibrary.Assemblies, filesToSkip),
                                              compileLibrary.Dependencies,
                                              compileLibrary.Serviceable,
                                              compileLibrary.Path,
                                              compileLibrary.HashPath);
                }
                else
                {
                    yield return compileLibrary;
                }
            }
        }

        private IEnumerable<string> TrimAssemblies(IEnumerable<string> assemblies, ISet<string> filesToTrim)
        {
            foreach (var assembly in assemblies)
            {
                if (!filesToTrim.Contains(assembly))
                {
                    yield return assembly;
                }
            }
        }
    }
}
