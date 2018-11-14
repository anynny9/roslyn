﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.LanguageServices.Implementation.TaskList;
using Roslyn.Utilities;

namespace Microsoft.VisualStudio.LanguageServices.Implementation.ProjectSystem
{
    internal sealed class VisualStudioProject
    {
        private readonly VisualStudioWorkspaceImpl _workspace;
        private readonly HostDiagnosticUpdateSource _hostDiagnosticUpdateSource;
        private readonly string _projectUniqueName;

        /// <summary>
        /// Provides dynamic source files for files added through <see cref="AddDynamicSourceFile" />.
        /// </summary>
        private readonly ImmutableArray<Lazy<IDynamicFileInfoProvider, FileExtensionsMetadata>> _dynamicFileInfoProviders;

        /// <summary>
        /// A gate taken for all mutation of any mutable field in this type.
        /// </summary>
        /// <remarks>This is, for now, intentionally pessimistic. There are no doubt ways that we could allow more to run in parallel,
        /// but the current tradeoff is for simplicity of code and "obvious correctness" than something that is subtle, fast, and wrong.</remarks>
        private readonly object _gate = new object();

        /// <summary>
        /// The number of active batch scopes. If this is zero, we are not batching, non-zero means we are batching.
        /// </summary>
        private int _activeBatchScopes = 0;

        private readonly List<(string path, MetadataReferenceProperties properties)> _metadataReferencesAddedInBatch = new List<(string path, MetadataReferenceProperties properties)>();
        private readonly List<(string path, MetadataReferenceProperties properties)> _metadataReferencesRemovedInBatch = new List<(string path, MetadataReferenceProperties properties)>();
        private readonly List<ProjectReference> _projectReferencesAddedInBatch = new List<ProjectReference>();
        private readonly List<ProjectReference> _projectReferencesRemovedInBatch = new List<ProjectReference>();

        private readonly Dictionary<string, VisualStudioAnalyzer> _analyzerPathsToAnalyzers = new Dictionary<string, VisualStudioAnalyzer>();
        private readonly List<VisualStudioAnalyzer> _analyzersAddedInBatch = new List<VisualStudioAnalyzer>();
        private readonly List<VisualStudioAnalyzer> _analyzersRemovedInBatch = new List<VisualStudioAnalyzer>();

        private readonly List<Func<Solution, Solution>> _projectPropertyModificationsInBatch = new List<Func<Solution, Solution>>();

        private string _assemblyName;
        private string _displayName;
        private string _filePath;
        private CompilationOptions _compilationOptions;
        private ParseOptions _parseOptions;
        private bool _hasAllInformation = true;
        private string _intermediateOutputFilePath;
        private string _outputFilePath;
        private string _outputRefFilePath;

        private readonly Dictionary<string, List<MetadataReferenceProperties>> _allMetadataReferences = new Dictionary<string, List<MetadataReferenceProperties>>();

        /// <summary>
        /// The file watching tokens for the documents in this project. We get the tokens even when we're in a batch, so the files here
        /// may not be in the actual workspace yet.
        /// </summary>
        private readonly Dictionary<DocumentId, FileChangeWatcher.IFileWatchingToken> _documentFileWatchingTokens = new Dictionary<DocumentId, FileChangeWatcher.IFileWatchingToken>();

        /// <summary>
        /// A file change context used to watch source files and additional files for this project. It's automatically set to watch the user's project
        /// directory so we avoid file-by-file watching.
        /// </summary>
        private readonly FileChangeWatcher.IContext _documentFileChangeContext;

        /// <summary>
        /// A file change context used to watch metadata and analyzer references.
        /// </summary>
        private readonly FileChangeWatcher.IContext _fileReferenceChangeContext;

        /// <summary>
        /// File watching tokens from <see cref="_fileReferenceChangeContext"/> that are watching metadata references. These are only created once we are actually applying a batch because
        /// we don't determine until the batch is applied if the file reference will actually be a file reference or it'll be a converted project reference.
        /// </summary>
        private readonly Dictionary<PortableExecutableReference, FileChangeWatcher.IFileWatchingToken> _metadataReferenceFileWatchingTokens = new Dictionary<PortableExecutableReference, FileChangeWatcher.IFileWatchingToken>();

        /// <summary>
        /// track whether we have been subscribed to <see cref="IDynamicFileInfoProvider.Updated"/> event
        /// </summary>
        private readonly HashSet<IDynamicFileInfoProvider> _eventSubscriptionTracker = new HashSet<IDynamicFileInfoProvider>();

        /// <summary>
        /// map original dynamic file path to <see cref="DynamicFileInfo.FilePath"/>
        /// 
        /// original dyanmic file path points to something like xxx.cshtml that are given to project system
        /// and <see cref="DynamicFileInfo.FilePath"/> points to a mapped file path provided by <see cref="IDynamicFileInfoProvider"/>
        /// and how and what it got mapped to is up to the provider. 
        /// 
        /// Workspace will only knows about <see cref="DynamicFileInfo.FilePath"/> but not the original dynamic file path
        /// </summary>
        private readonly Dictionary<string, string> _dynamicFilePathMaps = new Dictionary<string, string>();

        private readonly BatchingDocumentCollection _sourceFiles;
        private readonly BatchingDocumentCollection _additionalFiles;

        public ProjectId Id { get; }
        public string Language { get; }

        internal VisualStudioProject(
            VisualStudioWorkspaceImpl workspace,
            ImmutableArray<Lazy<IDynamicFileInfoProvider, FileExtensionsMetadata>> dynamicFileInfoProviders,
            HostDiagnosticUpdateSource hostDiagnosticUpdateSource,
            ProjectId id,
            string projectUniqueName,
            string language,
            string directoryNameOpt)
        {
            _workspace = workspace;
            _dynamicFileInfoProviders = dynamicFileInfoProviders;
            _hostDiagnosticUpdateSource = hostDiagnosticUpdateSource;

            Id = id;
            Language = language;
            _displayName = projectUniqueName;
            _projectUniqueName = projectUniqueName;

            if (directoryNameOpt != null)
            {
                // TODO: use directoryNameOpt to create a directory watcher. For now, there's perf hits due to the flood of events we'll need to sort out later.
                // _documentFileChangeContext = _workspace.FileChangeWatcher.CreateContextForDirectory(directoryNameOpt);
                _documentFileChangeContext = workspace.FileChangeWatcher.CreateContext();
            }
            else
            {
                _documentFileChangeContext = workspace.FileChangeWatcher.CreateContext();
            }

            _documentFileChangeContext.FileChanged += DocumentFileChangeContext_FileChanged;

            // TODO: set this to watch the NuGet directory or the reference assemblies directory; since those change rarely and most references
            // will come from them, we can avoid creating a bunch of explicit file watchers.
            _fileReferenceChangeContext = workspace.FileChangeWatcher.CreateContext();
            _fileReferenceChangeContext.FileChanged += FileReferenceChangeContext_FileChanged;

            _sourceFiles = new BatchingDocumentCollection(this, (s, d) => s.ContainsDocument(d), (w, d) => w.OnDocumentAdded(d), (w, documentId) => w.OnDocumentRemoved(documentId));
            _additionalFiles = new BatchingDocumentCollection(this, (s, d) => s.ContainsAdditionalDocument(d), (w, d) => w.OnAdditionalDocumentAdded(d), (w, documentId) => w.OnAdditionalDocumentRemoved(documentId));
        }

        private void ChangeProjectProperty<T>(ref T field, T newValue, Func<Solution, Solution> withNewValue, Action<Workspace> changeValue)
        {
            lock (_gate)
            {
                // If nothing is changing, we can skip entirely
                if (object.Equals(field, newValue))
                {
                    return;
                }

                field = newValue;

                if (_activeBatchScopes > 0)
                {
                    _projectPropertyModificationsInBatch.Add(withNewValue);
                }
                else
                {
                    _workspace.ApplyChangeToWorkspace(changeValue);
                }
            }
        }

        private void ChangeProjectOutputPath(ref string field, string newValue, Func<Solution, Solution> withNewValue, Action<Workspace> changeValue)
        {
            lock (_gate)
            {
                // Skip if nothing changing
                if (field == newValue)
                {
                    return;
                }

                if (field != null)
                {
                    _workspace.RemoveProjectOutputPath(Id, field);
                }

                if (newValue != null)
                {
                    _workspace.AddProjectOutputPath(Id, newValue);
                }

                ChangeProjectProperty(ref field, newValue, withNewValue, changeValue);
            }
        }
        public string AssemblyName
        {
            get => _assemblyName;
            set => ChangeProjectProperty(
                      ref _assemblyName,
                      value,
                       s => s.WithProjectAssemblyName(Id, value),
                       w => w.OnAssemblyNameChanged(Id, value));
        }

        public CompilationOptions CompilationOptions
        {
            get => _compilationOptions;
            set => ChangeProjectProperty(
                       ref _compilationOptions,
                       value,
                       s => s.WithProjectCompilationOptions(Id, value),
                       w => w.OnCompilationOptionsChanged(Id, value));
        }

        public ParseOptions ParseOptions
        {
            get => _parseOptions;
            set => ChangeProjectProperty(
                       ref _parseOptions,
                       value,
                       s => s.WithProjectParseOptions(Id, value),
                       w => w.OnParseOptionsChanged(Id, value));
        }

        /// <summary>
        /// The path to the output in obj.
        /// </summary>
        /// <remarks>This is internal for now, as it's only consumed by <see cref="EditAndContinue.VsENCRebuildableProjectImpl"/>
        /// which directly takes a <see cref="VisualStudioProject"/>.</remarks>
        internal string IntermediateOutputFilePath
        {
            get => _intermediateOutputFilePath;
            set
            {
                // Unlike OutputFilePath and OutputRefFilePath, the intermediate output path isn't represented in the workspace anywhere;
                // thus, we won't mutate the solution. We'll still call ChangeProjectOutputPath so we have the rest of the output path tracking
                // for any P2P reference conversion.
                ChangeProjectOutputPath(ref _intermediateOutputFilePath, value, s => s, w => { });
            }
        }

        public string OutputFilePath
        {
            get => _outputFilePath;
            set => ChangeProjectOutputPath(ref _outputFilePath,
                       value,
                       s => s.WithProjectOutputFilePath(Id, value),
                       w => w.OnOutputFilePathChanged(Id, value));
        }

        public string OutputRefFilePath
        {
            get => _outputRefFilePath;
            set => ChangeProjectOutputPath(ref _outputRefFilePath,
                       value,
                       s => s.WithProjectOutputRefFilePath(Id, value),
                       w => w.OnOutputRefFilePathChanged(Id, value));
        }

        public string FilePath
        {
            get => _filePath;
            set => ChangeProjectProperty(ref _filePath,
                       value,
                       s => s.WithProjectFilePath(Id, value),
                       w => w.OnProjectNameChanged(Id, _displayName, value));
        }

        public string DisplayName
        {
            get => _displayName;
            set => ChangeProjectProperty(ref _displayName,
                       value,
                       s => s.WithProjectName(Id, value),
                       w => w.OnProjectNameChanged(Id, value, _filePath));
        }

        // internal to match the visibility of the Workspace-level API -- this is something
        // we use but we haven't made officially public yet.
        internal bool HasAllInformation
        {
            get => _hasAllInformation;
            set => ChangeProjectProperty(ref _hasAllInformation,
                       value,
                       s => s.WithHasAllInformation(Id, value),
                       w => w.OnHasAllInformationChanged(Id, value));
        }


        #region Batching

        public BatchScope CreateBatchScope()
        {
            lock (_gate)
            {
                _activeBatchScopes++;
                return new BatchScope(this);
            }
        }

        public sealed class BatchScope : IDisposable
        {
            private readonly VisualStudioProject _project;

            /// <summary>
            /// Flag to control if this has already been disposed. Not a boolean only so it can be used with Interlocked.CompareExchange.
            /// </summary>
            private volatile int _disposed = 0;

            internal BatchScope(VisualStudioProject visualStudioProject)
            {
                _project = visualStudioProject;
            }

            public void Dispose()
            {
                if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
                {
                    _project.OnBatchScopeDisposed();
                }
            }
        }

        private void OnBatchScopeDisposed()
        {
            lock (_gate)
            {
                _activeBatchScopes--;

                if (_activeBatchScopes > 0)
                {
                    return;
                }

                var documentFileNamesAdded = ImmutableArray.CreateBuilder<string>();
                var documentsToOpen = new List<(DocumentId, SourceTextContainer)>();

                _workspace.ApplyBatchChangeToProject(Id, solution =>
                {
                    solution = _sourceFiles.UpdateSolutionForBatch(
                        solution,
                        documentFileNamesAdded,
                        documentsToOpen,
                        (s, documents) => solution.AddDocuments(documents),
                        (s, id) =>
                        {
                            // Clear any document-specific data now (like open file trackers, etc.). If we called OnRemoveDocument directly this is
                            // called, but since we're doing this in one large batch we need to do it now.
                            _workspace.ClearDocumentData(id);
                            return s.RemoveDocument(id);
                        });

                    solution = _additionalFiles.UpdateSolutionForBatch(
                        solution,
                        documentFileNamesAdded,
                        documentsToOpen,
                        (s, documents) =>
                        {
                            foreach (var document in documents)
                            {
                                s = s.AddAdditionalDocument(document);
                            }

                            return s;
                        },
                        (s, id) =>
                        {
                            // Clear any document-specific data now (like open file trackers, etc.). If we called OnRemoveDocument directly this is
                            // called, but since we're doing this in one large batch we need to do it now.
                            _workspace.ClearDocumentData(id);
                            return s.RemoveAdditionalDocument(id);
                        });

                    // Metadata reference adding...
                    if (_metadataReferencesAddedInBatch.Count > 0)
                    {
                        var projectReferencesCreated = new List<ProjectReference>();
                        var metadataReferencesCreated = new List<MetadataReference>();

                        foreach (var metadataReferenceAddedInBatch in _metadataReferencesAddedInBatch)
                        {
                            var projectReference = _workspace.TryCreateConvertedProjectReference(Id, metadataReferenceAddedInBatch.path, metadataReferenceAddedInBatch.properties);

                            if (projectReference != null)
                            {
                                projectReferencesCreated.Add(projectReference);
                            }
                            else
                            {
                                var metadataReference = _workspace.CreatePortableExecutableReference(metadataReferenceAddedInBatch.path, metadataReferenceAddedInBatch.properties);
                                metadataReferencesCreated.Add(metadataReference);
                                _metadataReferenceFileWatchingTokens.Add(metadataReference, _fileReferenceChangeContext.EnqueueWatchingFile(metadataReference.FilePath));
                            }
                        }

                        solution = solution.AddProjectReferences(Id, projectReferencesCreated)
                                           .AddMetadataReferences(Id, metadataReferencesCreated);

                        ClearAndZeroCapacity(_metadataReferencesAddedInBatch);
                    }

                    // Metadata reference removing...
                    foreach (var metadataReferenceRemovedInBatch in _metadataReferencesRemovedInBatch)
                    {
                        var projectReference = _workspace.TryRemoveConvertedProjectReference(Id, metadataReferenceRemovedInBatch.path, metadataReferenceRemovedInBatch.properties);

                        if (projectReference != null)
                        {
                            solution = solution.RemoveProjectReference(Id, projectReference);
                        }
                        else
                        {
                            // TODO: find a cleaner way to fetch this
                            var metadataReference = _workspace.CurrentSolution.GetProject(Id).MetadataReferences.Cast<PortableExecutableReference>()
                                                                                    .Single(m => m.FilePath == metadataReferenceRemovedInBatch.path && m.Properties == metadataReferenceRemovedInBatch.properties);

                            _fileReferenceChangeContext.StopWatchingFile(_metadataReferenceFileWatchingTokens[metadataReference]);
                            _metadataReferenceFileWatchingTokens.Remove(metadataReference);

                            solution = solution.RemoveMetadataReference(Id, metadataReference);
                        }
                    }

                    ClearAndZeroCapacity(_metadataReferencesRemovedInBatch);

                    // Project reference adding...
                    solution = solution.AddProjectReferences(Id, _projectReferencesAddedInBatch);
                    ClearAndZeroCapacity(_projectReferencesAddedInBatch);

                    // Project reference removing...
                    foreach (var projectReference in _projectReferencesRemovedInBatch)
                    {
                        solution = solution.RemoveProjectReference(Id, projectReference);
                    }

                    ClearAndZeroCapacity(_projectReferencesRemovedInBatch);

                    // Analyzer reference adding...
                    solution = solution.AddAnalyzerReferences(Id, _analyzersAddedInBatch.Select(a => a.GetReference()));
                    ClearAndZeroCapacity(_analyzersAddedInBatch);

                    // Analyzer reference removing...
                    foreach (var analyzerReference in _analyzersRemovedInBatch)
                    {
                        solution = solution.RemoveAnalyzerReference(Id, analyzerReference.GetReference());
                    }

                    ClearAndZeroCapacity(_analyzersRemovedInBatch);

                    // Other property modifications...
                    foreach (var propertyModification in _projectPropertyModificationsInBatch)
                    {
                        solution = propertyModification(solution);
                    }

                    ClearAndZeroCapacity(_projectPropertyModificationsInBatch);

                    return solution;
                });

                foreach (var (documentId, textContainer) in documentsToOpen)
                {
                    _workspace.ApplyChangeToWorkspace(w => w.OnDocumentOpened(documentId, textContainer));
                }

                // Check for those files being opened to start wire-up if necessary
                _workspace.CheckForOpenDocuments(documentFileNamesAdded.ToImmutable());
            }
        }

        #endregion

        #region Source File Addition/Removal

        public void AddSourceFile(string fullPath, SourceCodeKind sourceCodeKind = SourceCodeKind.Regular, ImmutableArray<string> folders = default)
        {
            _sourceFiles.AddFile(fullPath, sourceCodeKind, folders);
        }

        public DocumentId AddSourceTextContainer(
            SourceTextContainer textContainer,
            string fullPath,
            SourceCodeKind sourceCodeKind = SourceCodeKind.Regular,
            ImmutableArray<string> folders = default,
            IDocumentServiceProvider documentServiceProvider = null)
        {
            return _sourceFiles.AddTextContainer(textContainer, fullPath, sourceCodeKind, folders, documentServiceProvider);
        }

        public bool ContainsSourceFile(string fullPath)
        {
            return _sourceFiles.ContainsFile(fullPath);
        }

        public void RemoveSourceFile(string fullPath)
        {
            _sourceFiles.RemoveFile(fullPath);
        }

        public void RemoveSourceTextContainer(SourceTextContainer textContainer)
        {
            _sourceFiles.RemoveTextContainer(textContainer);
        }

        #endregion

        #region Additional File Addition/Removal

        // TODO: should AdditionalFiles have source code kinds?
        public void AddAdditionalFile(string fullPath, SourceCodeKind sourceCodeKind = SourceCodeKind.Regular)
        {
            _additionalFiles.AddFile(fullPath, sourceCodeKind, folders: default);
        }

        public bool ContainsAdditionalFile(string fullPath)
        {
            return _additionalFiles.ContainsFile(fullPath);
        }

        public void RemoveAdditionalFile(string fullPath)
        {
            _additionalFiles.RemoveFile(fullPath);
        }

        #endregion

        #region Non Source File Addition/Removal

        public void AddDynamicSourceFile(string dynamicFilePath, ImmutableArray<string> folders)
        {
            var extension = FileNameUtilities.GetExtension(dynamicFilePath)?.TrimStart('.');
            if (extension?.Length == 0)
            {
                return;
            }

            foreach (var provider in _dynamicFileInfoProviders)
            {
                // skip unrelated providers
                if (!provider.Metadata.Extensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                // don't get confused by _filePath and filePath
                // VisualStudioProject._filePath points to csproj/vbproj of the project
                // and the parameter filePath points to dynamic file such as cshtml and etc
                // 
                // also, provider is free-threaded. so fine to call Wait rather than JTF
                var fileInfo = provider.Value.GetDynamicFileInfoAsync(
                    projectId: Id, projectFilePath: _filePath, filePath: dynamicFilePath, CancellationToken.None).WaitAndGetResult_CanCallOnBackground(CancellationToken.None);

                if (fileInfo == null)
                {
                    continue;
                }

                fileInfo = FixUpDynamicFileInfo(fileInfo, dynamicFilePath);

                // remember map between original dynamic file path to DynamicFileInfo.FilePath
                _dynamicFilePathMaps.Add(dynamicFilePath, fileInfo.FilePath);
                _sourceFiles.AddDynamicFile(provider.Value, fileInfo, folders);
                return;
            }
        }

        private DynamicFileInfo FixUpDynamicFileInfo(DynamicFileInfo fileInfo, string filePath)
        {
            // we might change contract and just throw here. but for now, we keep existing contract where one can return null for DynamicFileInfo.FilePath
            if (string.IsNullOrEmpty(fileInfo.FilePath))
            {
                return new DynamicFileInfo(filePath, fileInfo.SourceCodeKind, fileInfo.TextLoader, fileInfo.DocumentServiceProvider);
            }

            return fileInfo;
        }

        public void RemoveDynamicSourceFile(string dynamicFilePath)
        {
            var provider = _sourceFiles.RemoveDynamicFile(_dynamicFilePathMaps[dynamicFilePath]);

            // provider is free-threaded. so fine to call Wait rather than JTF
            provider.RemoveDynamicFileInfoAsync(
                projectId: Id, projectFilePath: _filePath, filePath: dynamicFilePath, CancellationToken.None).Wait(CancellationToken.None);
        }

        private void OnDynamicFileInfoUpdated(object sender, string dynamicFilePath)
        {
            if (!_dynamicFilePathMaps.TryGetValue(dynamicFilePath, out var fileInfoPath))
            {
                // given file doesn't belong to this project. 
                // this happen since the event this is handling is shared between all projects
                return;
            }

            _sourceFiles.ProcessFileChange(dynamicFilePath, fileInfoPath);
        }

        #endregion

        #region Analyzer Addition/Removal

        public void AddAnalyzerReference(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
            {
                throw new ArgumentException("message", nameof(fullPath));
            }

            var visualStudioAnalyzer = new VisualStudioAnalyzer(
                fullPath,
                _hostDiagnosticUpdateSource,
                Id,
                _workspace,
                Language);

            lock (_gate)
            {
                if (_analyzerPathsToAnalyzers.ContainsKey(fullPath))
                {
                    throw new ArgumentException($"'{fullPath}' has already been added to this project.", nameof(fullPath));
                }

                _analyzerPathsToAnalyzers.Add(fullPath, visualStudioAnalyzer);

                if (_activeBatchScopes > 0)
                {
                    _analyzersAddedInBatch.Add(visualStudioAnalyzer);
                }
                else
                {
                    _workspace.ApplyChangeToWorkspace(w => w.OnAnalyzerReferenceAdded(Id, visualStudioAnalyzer.GetReference()));
                }
            }
        }

        public void RemoveAnalyzerReference(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
            {
                throw new ArgumentException("message", nameof(fullPath));
            }

            lock (_gate)
            {
                if (!_analyzerPathsToAnalyzers.TryGetValue(fullPath, out var visualStudioAnalyzer))
                {
                    throw new ArgumentException($"'{fullPath}' is not an analyzer of this project.", nameof(fullPath));
                }

                _analyzerPathsToAnalyzers.Remove(fullPath);

                if (_activeBatchScopes > 0)
                {
                    _analyzersRemovedInBatch.Add(visualStudioAnalyzer);
                }
                else
                {
                    _workspace.ApplyChangeToWorkspace(w => w.OnAnalyzerReferenceRemoved(Id, visualStudioAnalyzer.GetReference()));
                }
            }
        }

        #endregion

        private void DocumentFileChangeContext_FileChanged(object sender, string fullFilePath)
        {
            _sourceFiles.ProcessFileChange(fullFilePath);
            _additionalFiles.ProcessFileChange(fullFilePath);
        }

        private void FileReferenceChangeContext_FileChanged(object sender, string fullFilePath)
        {
            lock (_gate)
            {
                // Since all adds/removals of references for this project happen under our lock, it's safe to do this
                // check without taking the main workspace lock.
                var project = _workspace.CurrentSolution.GetProject(Id);

                foreach (var portableExecutableReference in project.MetadataReferences.OfType<PortableExecutableReference>())
                {
                    if (portableExecutableReference.FilePath == fullFilePath)
                    {
                        var newPortableExecutableReference = _workspace.CreatePortableExecutableReference(portableExecutableReference.FilePath, portableExecutableReference.Properties);

                        // We need to swap this out. Time to take the full lock now.
                        _workspace.ApplyBatchChangeToProject(Id, s =>
                        {
                            return s.RemoveMetadataReference(Id, portableExecutableReference)
                                    .AddMetadataReference(Id, newPortableExecutableReference);
                        });

                        // Transfer the ownership of the file watching token
                        var fileWatchingToken = _metadataReferenceFileWatchingTokens[portableExecutableReference];
                        _metadataReferenceFileWatchingTokens.Remove(portableExecutableReference);
                        _metadataReferenceFileWatchingTokens.Add(newPortableExecutableReference, fileWatchingToken);
                    }
                }
            }
        }


        #region Metadata Reference Addition/Removal

        public void AddMetadataReference(string fullPath, MetadataReferenceProperties properties)
        {
            if (string.IsNullOrEmpty(fullPath))
            {
                throw new ArgumentException($"{nameof(fullPath)} isn't a valid path.", nameof(fullPath));
            }

            lock (_gate)
            {
                if (ContainsMetadataReference(fullPath, properties))
                {
                    throw new InvalidOperationException("The metadata reference has already been added to the project.");
                }

                _allMetadataReferences.MultiAdd(fullPath, properties);

                if (_activeBatchScopes > 0)
                {
                    if (!_metadataReferencesRemovedInBatch.Remove((fullPath, properties)))
                    {
                        _metadataReferencesAddedInBatch.Add((fullPath, properties));
                    }
                }
                else
                {
                    _workspace.ApplyChangeToWorkspace(w =>
                    {
                        var projectReference = _workspace.TryCreateConvertedProjectReference(Id, fullPath, properties);

                        if (projectReference != null)
                        {
                            w.OnProjectReferenceAdded(Id, projectReference);
                        }
                        else
                        {
                            var metadataReference = _workspace.CreatePortableExecutableReference(fullPath, properties);
                            w.OnMetadataReferenceAdded(Id, metadataReference);
                            _metadataReferenceFileWatchingTokens.Add(metadataReference, _fileReferenceChangeContext.EnqueueWatchingFile(metadataReference.FilePath));
                        }
                    });
                }
            }
        }

        public bool ContainsMetadataReference(string fullPath, MetadataReferenceProperties properties)
        {
            lock (_gate)
            {
                return GetPropertiesForMetadataReference(fullPath).Contains(properties);
            }
        }

        /// <summary>
        /// Returns the properties being used for the current metadata reference added to this project. May return multiple properties if
        /// the reference has been added multiple times with different properties.
        /// </summary>
        public ImmutableArray<MetadataReferenceProperties> GetPropertiesForMetadataReference(string fullPath)
        {
            lock (_gate)
            {
                _allMetadataReferences.TryGetValue(fullPath, out var list);

                // Note: AsImmutableOrEmpty accepts null recievers and treats that as an empty array
                return list.AsImmutableOrEmpty();
            }
        }

        public void RemoveMetadataReference(string fullPath, MetadataReferenceProperties properties)
        {
            if (string.IsNullOrEmpty(fullPath))
            {
                throw new ArgumentException($"{nameof(fullPath)} isn't a valid path.", nameof(fullPath));
            }

            lock (_gate)
            {
                if (!ContainsMetadataReference(fullPath, properties))
                {
                    throw new InvalidOperationException("The metadata reference does not exist in this project.");
                }

                _allMetadataReferences.MultiRemove(fullPath, properties);

                if (_activeBatchScopes > 0)
                {
                    if (!_metadataReferencesAddedInBatch.Remove((fullPath, properties)))
                    {
                        _metadataReferencesRemovedInBatch.Add((fullPath, properties));
                    }
                }
                else
                {
                    _workspace.ApplyChangeToWorkspace(w =>
                    {
                        var projectReference = _workspace.TryRemoveConvertedProjectReference(Id, fullPath, properties);

                        // If this was converted to a project reference, we have now recorded the removal -- let's remove it here too
                        if (projectReference != null)
                        {
                            w.OnProjectReferenceRemoved(Id, projectReference);
                        }
                        else
                        {
                            // TODO: find a cleaner way to fetch this
                            var metadataReference = w.CurrentSolution.GetProject(Id).MetadataReferences.Cast<PortableExecutableReference>()
                                                                                    .Single(m => m.FilePath == fullPath && m.Properties == properties);

                            w.OnMetadataReferenceRemoved(Id, metadataReference);

                            _fileReferenceChangeContext.StopWatchingFile(_metadataReferenceFileWatchingTokens[metadataReference]);
                            _metadataReferenceFileWatchingTokens.Remove(metadataReference);
                        }
                    });
                }
            }
        }

        #endregion

        #region Project Reference Addition/Removal

        public void AddProjectReference(ProjectReference projectReference)
        {
            if (projectReference == null)
            {
                throw new ArgumentNullException(nameof(projectReference));
            }

            lock (_gate)
            {
                if (ContainsProjectReference(projectReference))
                {
                    throw new ArgumentException("The project reference has already been added to the project.");
                }

                if (_activeBatchScopes > 0)
                {
                    if (!_projectReferencesRemovedInBatch.Remove(projectReference))
                    {
                        _projectReferencesAddedInBatch.Add(projectReference);
                    }
                }
                else
                {
                    _workspace.ApplyChangeToWorkspace(w => w.OnProjectReferenceAdded(Id, projectReference));
                }
            }
        }

        public bool ContainsProjectReference(ProjectReference projectReference)
        {
            if (projectReference == null)
            {
                throw new ArgumentNullException(nameof(projectReference));
            }

            lock (_gate)
            {
                if (_projectReferencesRemovedInBatch.Contains(projectReference))
                {
                    return false;
                }

                if (_projectReferencesAddedInBatch.Contains(projectReference))
                {
                    return true;
                }

                return _workspace.CurrentSolution.GetProject(Id).AllProjectReferences.Contains(projectReference);
            }
        }

        public IReadOnlyList<ProjectReference> GetProjectReferences()
        {
            lock (_gate)
            {
                // If we're not batching, then this is cheap: just fetch from the workspace and we're done
                var projectReferencesInWorkspace = _workspace.CurrentSolution.GetProject(Id).AllProjectReferences;

                if (_activeBatchScopes == 0)
                {
                    return projectReferencesInWorkspace;
                }

                // Not, so we get to compute a new list instead
                var newList = projectReferencesInWorkspace.ToList();
                newList.AddRange(_projectReferencesAddedInBatch);
                newList.RemoveAll(p => _projectReferencesRemovedInBatch.Contains(p));

                return newList;
            }
        }

        public void RemoveProjectReference(ProjectReference projectReference)
        {
            if (projectReference == null)
            {
                throw new ArgumentNullException(nameof(projectReference));
            }

            lock (_gate)
            {
                if (_activeBatchScopes > 0)
                {
                    if (!_projectReferencesAddedInBatch.Remove(projectReference))
                    {
                        _projectReferencesRemovedInBatch.Add(projectReference);
                    }
                }
                else
                {
                    _workspace.ApplyChangeToWorkspace(w => w.OnProjectReferenceRemoved(Id, projectReference));
                }
            }
        }

        #endregion

        public void RemoveFromWorkspace()
        {
            _documentFileChangeContext.Dispose();

            lock (_gate)
            {
                // clear tracking to external components
                foreach (var provider in _eventSubscriptionTracker)
                {
                    provider.Updated -= OnDynamicFileInfoUpdated;
                }

                _eventSubscriptionTracker.Clear();
            }

            _workspace.ApplyChangeToWorkspace(w => w.OnProjectRemoved(Id));
        }

        /// <summary>
        /// Adds an additional output path that can be used for automatic conversion of metadata references to P2P references.
        /// Any projects with metadata references to the path given here will be converted to project-to-project references.
        /// </summary>
        public void AddOutputPath(string outputPath)
        {
            if (string.IsNullOrEmpty(outputPath))
            {
                throw new ArgumentException($"{nameof(outputPath)} isn't a valid path.", nameof(outputPath));
            }

            _workspace.AddProjectOutputPath(Id, outputPath);
        }

        /// <summary>
        /// Removes an additional output path that was added by <see cref="AddOutputPath(string)"/>.
        /// </summary>
        public void RemoveOutputPath(string outputPath)
        {
            if (string.IsNullOrEmpty(outputPath))
            {
                throw new ArgumentException($"{nameof(outputPath)} isn't a valid path.", nameof(outputPath));
            }

            _workspace.RemoveProjectOutputPath(Id, outputPath);
        }

        /// <summary>
        /// Clears a list and zeros out the capacity. The lists we use for batching are likely to get large during an initial load, but after
        /// that point should never get that large again.
        /// </summary>
        private static void ClearAndZeroCapacity<T>(List<T> list)
        {
            list.Clear();
            list.Capacity = 0;
        }

        /// <summary>
        /// Clears a list and zeros out the capacity. The lists we use for batching are likely to get large during an initial load, but after
        /// that point should never get that large again.
        /// </summary>
        private static void ClearAndZeroCapacity<T>(ImmutableArray<T>.Builder list)
        {
            list.Clear();
            list.Capacity = 0;
        }

        /// <summary>
        /// Helper class to manage collections of source-file like things; this exists just to avoid duplicating all the logic for regular source files
        /// and additional files.
        /// </summary>
        /// <remarks>This class should be free-threaded, and any synchronization is done via <see cref="VisualStudioProject._gate"/>.
        /// This class is otehrwise free to operate on private members of <see cref="_project"/> if needed.</remarks>
        private sealed class BatchingDocumentCollection
        {
            private readonly VisualStudioProject _project;

            /// <summary>
            /// The map of file paths to the underlying <see cref="DocumentId"/>. This document may exist in <see cref="_documentsAddedInBatch"/> or has been
            /// pushed to the actual workspace.
            /// </summary>
            private readonly Dictionary<string, DocumentId> _documentPathsToDocumentIds = new Dictionary<string, DocumentId>(StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// A map of explicitly-added "always open" <see cref="SourceTextContainer"/> and their associated <see cref="DocumentId"/>. This does not contain
            /// any regular files that have been open.
            /// </summary>
            private IBidirectionalMap<SourceTextContainer, DocumentId> _sourceTextContainersToDocumentIds = BidirectionalMap<SourceTextContainer, DocumentId>.Empty;

            /// <summary>
            /// The map of <see cref="DocumentId"/> to <see cref="IDynamicFileInfoProvider"/> whose <see cref="DynamicFileInfo"/> got added into <see cref="Workspace"/>
            /// </summary>
            private readonly Dictionary<DocumentId, IDynamicFileInfoProvider> _documentIdToDynamicFileInfoProvider = new Dictionary<DocumentId, IDynamicFileInfoProvider>();

            /// <summary>
            /// The current list of documents that are to be added in this batch.
            /// </summary>
            private readonly ImmutableArray<DocumentInfo>.Builder _documentsAddedInBatch = ImmutableArray.CreateBuilder<DocumentInfo>();

            /// <summary>
            /// The current list of documents that are being removed in this batch. Once the document is in this list, it is no longer in <see cref="_documentPathsToDocumentIds"/>.
            /// </summary>
            private readonly List<DocumentId> _documentsRemovedInBatch = new List<DocumentId>();

            private readonly Func<Solution, DocumentId, bool> _documentAlreadyInWorkspace;
            private readonly Action<Workspace, DocumentInfo> _documentAddAction;
            private readonly Action<Workspace, DocumentId> _documentRemoveAction;

            public BatchingDocumentCollection(VisualStudioProject project,
                Func<Solution, DocumentId, bool> documentAlreadyInWorkspace,
                Action<Workspace, DocumentInfo> documentAddAction,
                Action<Workspace, DocumentId> documentRemoveAction)
            {
                _project = project;
                _documentAlreadyInWorkspace = documentAlreadyInWorkspace;
                _documentAddAction = documentAddAction;
                _documentRemoveAction = documentRemoveAction;
            }

            public DocumentId AddFile(string fullPath, SourceCodeKind sourceCodeKind, ImmutableArray<string> folders)
            {
                if (string.IsNullOrEmpty(fullPath))
                {
                    throw new ArgumentException($"{nameof(fullPath)} isn't a valid path.", nameof(fullPath));
                }

                var documentId = DocumentId.CreateNewId(_project.Id, fullPath);
                var textLoader = new FileTextLoader(fullPath, defaultEncoding: null);
                var documentInfo = DocumentInfo.Create(
                    documentId,
                    FileNameUtilities.GetFileName(fullPath),
                    folders: folders.IsDefault ? null : (IEnumerable<string>)folders,
                    sourceCodeKind: sourceCodeKind,
                    loader: textLoader,
                    filePath: fullPath,
                    isGenerated: false);

                lock (_project._gate)
                {
                    if (_documentPathsToDocumentIds.ContainsKey(fullPath))
                    {
                        throw new ArgumentException($"'{fullPath}' has already been added to this project.", nameof(fullPath));
                    }

                    _documentPathsToDocumentIds.Add(fullPath, documentId);
                    _project._documentFileWatchingTokens.Add(documentId, _project._documentFileChangeContext.EnqueueWatchingFile(fullPath));

                    if (_project._activeBatchScopes > 0)
                    {
                        _documentsAddedInBatch.Add(documentInfo);
                    }
                    else
                    {
                        _project._workspace.ApplyChangeToWorkspace(w => _documentAddAction(w, documentInfo));
                        _project._workspace.CheckForOpenDocuments(ImmutableArray.Create(fullPath));
                    }
                }

                return documentId;
            }

            public DocumentId AddTextContainer(SourceTextContainer textContainer, string fullPath, SourceCodeKind sourceCodeKind, ImmutableArray<string> folders, IDocumentServiceProvider documentServiceProvider)
            {
                if (textContainer == null)
                {
                    throw new ArgumentNullException(nameof(textContainer));
                }

                var documentId = DocumentId.CreateNewId(_project.Id, fullPath);
                var textLoader = new SourceTextLoader(textContainer, fullPath);
                var documentInfo = DocumentInfo.Create(
                    documentId,
                    FileNameUtilities.GetFileName(fullPath),
                    folders: folders.IsDefault ? null : (IEnumerable<string>)folders,
                    sourceCodeKind: sourceCodeKind,
                    loader: textLoader,
                    filePath: fullPath,
                    isGenerated: false,
                    documentServiceProvider: documentServiceProvider);

                lock (_project._gate)
                {
                    if (_sourceTextContainersToDocumentIds.ContainsKey(textContainer))
                    {
                        throw new ArgumentException($"{nameof(textContainer)} is already added to this project.", nameof(textContainer));
                    }

                    if (fullPath != null)
                    {
                        if (_documentPathsToDocumentIds.ContainsKey(fullPath))
                        {
                            throw new ArgumentException($"'{fullPath}' has already been added to this project.");
                        }

                        _documentPathsToDocumentIds.Add(fullPath, documentId);
                    }

                    _sourceTextContainersToDocumentIds = _sourceTextContainersToDocumentIds.Add(textContainer, documentInfo.Id);

                    if (_project._activeBatchScopes > 0)
                    {
                        _documentsAddedInBatch.Add(documentInfo);
                    }
                    else
                    {
                        _project._workspace.ApplyChangeToWorkspace(w =>
                        {
                            _project._workspace.AddDocumentToDocumentsNotFromFiles(documentInfo.Id);
                            _documentAddAction(w, documentInfo);
                            w.OnDocumentOpened(documentInfo.Id, textContainer);
                        });
                    }
                }

                return documentId;
            }

            public void AddDynamicFile(IDynamicFileInfoProvider fileInfoProvider, DynamicFileInfo fileInfo, ImmutableArray<string> folders)
            {
                var documentInfo = CreateDocumentInfoFromFileInfo(fileInfo, folders.NullToEmpty());
                var documentId = documentInfo.Id;

                lock (_project._gate)
                {
                    var filePath = documentInfo.FilePath;
                    if (_documentPathsToDocumentIds.ContainsKey(filePath))
                    {
                        throw new ArgumentException($"'{filePath}' has already been added to this project.", nameof(filePath));
                    }

                    _documentPathsToDocumentIds.Add(filePath, documentId);

                    _documentIdToDynamicFileInfoProvider.Add(documentId, fileInfoProvider);

                    if (_project._eventSubscriptionTracker.Add(fileInfoProvider))
                    {
                        // subscribe to the event when we use this provider the first time
                        fileInfoProvider.Updated += _project.OnDynamicFileInfoUpdated;
                    }

                    if (_project._activeBatchScopes > 0)
                    {
                        _documentsAddedInBatch.Add(documentInfo);
                    }
                    else
                    {
                        // right now, assumption is dynamically generated file can never be opened in editor
                        _project._workspace.ApplyChangeToWorkspace(w => _documentAddAction(w, documentInfo));
                    }
                }
            }

            public IDynamicFileInfoProvider RemoveDynamicFile(string fullPath)
            {
                if (string.IsNullOrEmpty(fullPath))
                {
                    throw new ArgumentException($"{nameof(fullPath)} isn't a valid path.", nameof(fullPath));
                }

                lock (_project._gate)
                {
                    if (!_documentPathsToDocumentIds.TryGetValue(fullPath, out var documentId) ||
                        !_documentIdToDynamicFileInfoProvider.TryGetValue(documentId, out var fileInfoProvider))
                    {
                        throw new ArgumentException($"'{fullPath}' is not a dynamic file of this project.");
                    }

                    _documentIdToDynamicFileInfoProvider.Remove(documentId);

                    RemoveFileInternal(documentId, fullPath);

                    return fileInfoProvider;
                }
            }

            public void RemoveFile(string fullPath)
            {
                if (string.IsNullOrEmpty(fullPath))
                {
                    throw new ArgumentException($"{nameof(fullPath)} isn't a valid path.", nameof(fullPath));
                }

                lock (_project._gate)
                {
                    if (!_documentPathsToDocumentIds.TryGetValue(fullPath, out var documentId))
                    {
                        throw new ArgumentException($"'{fullPath}' is not a source file of this project.");
                    }

                    _project._documentFileChangeContext.StopWatchingFile(_project._documentFileWatchingTokens[documentId]);
                    _project._documentFileWatchingTokens.Remove(documentId);

                    RemoveFileInternal(documentId, fullPath);
                }
            }

            private void RemoveFileInternal(DocumentId documentId, string fullPath)
            {
                _documentPathsToDocumentIds.Remove(fullPath);

                // There are two cases:
                // 
                // 1. This file is actually been pushed to the workspace, and we need to remove it (either
                //    as a part of the active batch or immediately)
                // 2. It hasn't been pushed yet, but is contained in _documentsAddedInBatch
                if (_documentAlreadyInWorkspace(_project._workspace.CurrentSolution, documentId))
                {
                    if (_project._activeBatchScopes > 0)
                    {
                        _documentsRemovedInBatch.Add(documentId);
                    }
                    else
                    {
                        _project._workspace.ApplyChangeToWorkspace(w => _documentRemoveAction(w, documentId));
                    }
                }
                else
                {
                    for (int i = 0; i < _documentsAddedInBatch.Count; i++)
                    {
                        if (_documentsAddedInBatch[i].Id == documentId)
                        {
                            _documentsAddedInBatch.RemoveAt(i);
                            break;
                        }
                    }
                }
            }

            public void RemoveTextContainer(SourceTextContainer textContainer)
            {
                if (textContainer == null)
                {
                    throw new ArgumentNullException(nameof(textContainer));
                }

                lock (_project._gate)
                {
                    if (!_sourceTextContainersToDocumentIds.TryGetValue(textContainer, out var documentId))
                    {
                        throw new ArgumentException($"{nameof(textContainer)} is not a text container added to this project.");
                    }

                    _sourceTextContainersToDocumentIds = _sourceTextContainersToDocumentIds.RemoveKey(textContainer);

                    // if the TextContainer had a full path provided, remove it from the map.
                    var entry = _documentPathsToDocumentIds.Where(kv => kv.Value == documentId).FirstOrDefault();
                    if (entry.Key != null)
                    {
                        _documentPathsToDocumentIds.Remove(entry.Key);
                    }

                    // There are two cases:
                    // 
                    // 1. This file is actually been pushed to the workspace, and we need to remove it (either
                    //    as a part of the active batch or immediately)
                    // 2. It hasn't been pushed yet, but is contained in _documentsAddedInBatch
                    if (_project._workspace.CurrentSolution.GetDocument(documentId) != null)
                    {
                        if (_project._activeBatchScopes > 0)
                        {
                            _documentsRemovedInBatch.Add(documentId);
                        }
                        else
                        {
                            _project._workspace.ApplyChangeToWorkspace(w =>
                            {
                                w.OnDocumentClosed(documentId, new SourceTextLoader(textContainer, filePath: null));
                                _documentRemoveAction(w, documentId);
                                _project._workspace.RemoveDocumentToDocumentsNotFromFiles(documentId);
                            });
                        }
                    }
                    else
                    {
                        for (int i = 0; i < _documentsAddedInBatch.Count; i++)
                        {
                            if (_documentsAddedInBatch[i].Id == documentId)
                            {
                                _documentsAddedInBatch.RemoveAt(i);
                                break;
                            }
                        }
                    }
                }
            }

            public bool ContainsFile(string fullPath)
            {
                if (string.IsNullOrEmpty(fullPath))
                {
                    throw new ArgumentException($"{nameof(fullPath)} isn't a valid path.", nameof(fullPath));
                }

                lock (_project._gate)
                {
                    return _documentPathsToDocumentIds.ContainsKey(fullPath);
                }
            }

            public void ProcessFileChange(string filePath)
            {
                ProcessFileChange(filePath, filePath);
            }

            /// <summary>
            /// Process file content changes
            /// </summary>
            /// <param name="projectSystemFilePath">filepath given from project system</param>
            /// <param name="workspaceFilePath">filepath used in workspace. it might be different than projectSystemFilePath. ex) dynamic file</param>
            public void ProcessFileChange(string projectSystemFilePath, string workspaceFilePath)
            {
                lock (_project._gate)
                {
                    if (_documentPathsToDocumentIds.TryGetValue(workspaceFilePath, out var documentId))
                    {
                        // We create file watching prior to pushing the file to the workspace in batching, so it's
                        // possible we might see a file change notification early. In this case, toss it out. Since
                        // all adds/removals of documents for this project happen under our lock, it's safe to do this
                        // check without taking the main workspace lock
                        var document = _project._workspace.CurrentSolution.GetDocument(documentId);
                        if (document == null)
                        {
                            return;
                        }

                        _documentIdToDynamicFileInfoProvider.TryGetValue(documentId, out var fileInfoProvider);

                        _project._workspace.ApplyChangeToWorkspace(w =>
                        {
                            if (w.IsDocumentOpen(documentId))
                            {
                                return;
                            }

                            TextLoader textLoader;
                            IDocumentServiceProvider documentServiceProvider;
                            if (fileInfoProvider == null)
                            {
                                textLoader = new FileTextLoader(projectSystemFilePath, defaultEncoding: null);
                                documentServiceProvider = null;
                            }
                            else
                            {
                                // we do not expect JTF to be used around this code path. and contract of fileInfoProvider is it being real free-threaded
                                // meaning it can't use JTF to go back to UI thread.
                                // so, it is okay for us to call regular ".Result" on a task here.
                                var fileInfo = fileInfoProvider.GetDynamicFileInfoAsync(
                                    _project.Id, _project._filePath, projectSystemFilePath, CancellationToken.None).WaitAndGetResult_CanCallOnBackground(CancellationToken.None);

                                textLoader = fileInfo.TextLoader;
                                documentServiceProvider = fileInfo.DocumentServiceProvider;
                            }

                            var documentInfo = DocumentInfo.Create(
                                document.Id,
                                document.Name,
                                document.Folders,
                                document.SourceCodeKind,
                                loader: textLoader,
                                document.FilePath,
                                document.State.Attributes.IsGenerated,
                                documentServiceProvider: documentServiceProvider);

                            w.OnDocumentReloaded(documentInfo);
                        });
                    }
                }
            }

            internal Solution UpdateSolutionForBatch(
                Solution solution,
                ImmutableArray<string>.Builder documentFileNamesAdded,
                List<(DocumentId, SourceTextContainer)> documentsToOpen,
                Func<Solution, ImmutableArray<DocumentInfo>, Solution> addDocuments,
                Func<Solution, DocumentId, Solution> removeDocument)
            {
                // Document adding...
                solution = addDocuments(solution, _documentsAddedInBatch.ToImmutable());

                foreach (var documentInfo in _documentsAddedInBatch)
                {
                    documentFileNamesAdded.Add(documentInfo.FilePath);

                    if (_sourceTextContainersToDocumentIds.TryGetKey(documentInfo.Id, out var textContainer))
                    {
                        documentsToOpen.Add((documentInfo.Id, textContainer));
                    }
                }

                ClearAndZeroCapacity(_documentsAddedInBatch);

                // Document removing...
                foreach (var documentId in _documentsRemovedInBatch)
                {
                    solution = removeDocument(solution, documentId);
                }

                ClearAndZeroCapacity(_documentsRemovedInBatch);

                return solution;
            }

            private DocumentInfo CreateDocumentInfoFromFileInfo(DynamicFileInfo fileInfo, IEnumerable<string> folders)
            {
                // we use this file path for editorconfig. 
                var filePath = fileInfo.FilePath;

                var name = FileNameUtilities.GetFileName(filePath);
                var documentId = DocumentId.CreateNewId(_project.Id, filePath);

                var textLoader = fileInfo.TextLoader;
                var documentServiceProvider = fileInfo.DocumentServiceProvider;

                return DocumentInfo.Create(
                    documentId,
                    name,
                    folders: folders,
                    sourceCodeKind: fileInfo.SourceCodeKind,
                    loader: textLoader,
                    filePath: filePath,
                    isGenerated: false,
                    documentServiceProvider: documentServiceProvider);
            }

            private sealed class SourceTextLoader : TextLoader
            {
                private readonly SourceTextContainer _textContainer;
                private readonly string _filePath;

                public SourceTextLoader(SourceTextContainer textContainer, string filePath)
                {
                    _textContainer = textContainer;
                    _filePath = filePath;
                }

                public override Task<TextAndVersion> LoadTextAndVersionAsync(Workspace workspace, DocumentId documentId, CancellationToken cancellationToken)
                {
                    return Task.FromResult(TextAndVersion.Create(_textContainer.CurrentText, VersionStamp.Create(), _filePath));
                }
            }
        }
    }
}
