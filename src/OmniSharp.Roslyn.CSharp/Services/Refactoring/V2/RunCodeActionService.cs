using System.Collections.Generic;
using System.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.Extensions.Logging;
using OmniSharp.Mef;
using OmniSharp.Models;
using OmniSharp.Roslyn.CSharp.Extensions;
using OmniSharp.Services;

using RunCodeActionRequest = OmniSharp.Models.V2.RunCodeActionRequest;
using RunCodeActionResponse = OmniSharp.Models.V2.RunCodeActionResponse;

namespace OmniSharp.Roslyn.CSharp.Services.Refactoring.V2
{
    [OmniSharpHandler(OmnisharpEndpoints.V2.RunCodeAction, LanguageNames.CSharp)]
    public class RunCodeActionService : RequestHandler<RunCodeActionRequest, RunCodeActionResponse>
    {
        private readonly OmniSharpWorkspace _workspace;
        private readonly IEnumerable<ICodeActionProvider> _codeActionProviders;
        private readonly ILogger _logger;

        [ImportingConstructor]
        public RunCodeActionService(OmniSharpWorkspace workspace, [ImportMany] IEnumerable<ICodeActionProvider> providers, ILoggerFactory loggerFactory)
        {
            _workspace = workspace;
            _codeActionProviders = providers;
            _logger = loggerFactory.CreateLogger<RunCodeActionService>();
        }

        public async Task<RunCodeActionResponse> Handle(RunCodeActionRequest request)
        {
            var actions = await CodeActionHelper.GetActions(_workspace, _codeActionProviders, _logger, request);
            var action = actions.FirstOrDefault(a => a.GetIdentifier().Equals(request.Identifier));
            if (action == null)
            {
                return new RunCodeActionResponse();
            }

            _logger.LogInformation($"Applying code action: {action.Title}");

            var operations = await action.GetOperationsAsync(CancellationToken.None);

            var solution = _workspace.CurrentSolution;
            var changes = new List<ModifiedFileResponse>();
            var directory = Path.GetDirectoryName(request.FileName);

            foreach (var o in operations)
            {
                var applyChangesOperation = o as ApplyChangesOperation;
                if (applyChangesOperation != null)
                {
                    var fileChanges = await GetFileChangesAsync(applyChangesOperation.ChangedSolution, solution, directory, request.WantsTextChanges);

                    changes.AddRange(fileChanges);
                    solution = applyChangesOperation.ChangedSolution;
                }
            }

            if (request.ApplyTextChanges)
            {
                // Will this fail if FileChanges.GetFileChangesAsync(...) added files to the workspace?
                _workspace.TryApplyChanges(solution);
            }

            return new RunCodeActionResponse
            {
                Changes = changes
            };
        }

        private async Task<IEnumerable<ModifiedFileResponse>> GetFileChangesAsync(Solution newSolution, Solution oldSolution, string directory, bool wantTextChanges)
        {
            var filePathToResponseMap = new Dictionary<string, ModifiedFileResponse>();
            var solutionChanges = newSolution.GetChanges(oldSolution);

            foreach (var projectChange in solutionChanges.GetProjectChanges())
            {
                // Handle added documents
                foreach (var documentId in projectChange.GetAddedDocuments())
                {
                    var newDocument = newSolution.GetDocument(documentId);
                    var text = await newDocument.GetTextAsync();

                    var newFilePath = newDocument.FilePath == null || !Path.IsPathRooted(newDocument.FilePath)
                        ? Path.Combine(directory, newDocument.Name)
                        : newDocument.FilePath;

                    var modifiedFileResponse = new ModifiedFileResponse(newFilePath)
                    {
                        Changes = new[] {
                            new LinePositionSpanTextChange
                            {
                                NewText = text.ToString()
                            }
                        }
                    };

                    filePathToResponseMap[newFilePath] = modifiedFileResponse;

                    // We must add new files to the workspace to ensure that they're present when the host editor
                    // tries to modify them. This is a strange interaction because the workspace could be left
                    // in an incomplete state if the host editor doesn't apply changes to the new file, but it's
                    // what we've got today.
                    if (_workspace.GetDocument(newFilePath) == null)
                    {
                        var fileInfo = new FileInfo(newFilePath);
                        if (!fileInfo.Exists)
                        {
                            fileInfo.CreateText().Dispose();
                        }
                        else
                        {
                            // The file already exists on disk? Ensure that it's zero-length. If so, we can still use it.
                            if (fileInfo.Length > 0)
                            {
                                _logger.LogError($"File already exists on disk: '{newFilePath}'");
                                break;
                            }
                        }

                        _workspace.AddDocument(projectChange.ProjectId, newFilePath, newDocument.SourceCodeKind);
                    }
                    else
                    {
                        // The file already exists in the workspace? We're in a bad state.
                        _logger.LogError($"File already exists in workspace: '{newFilePath}'");
                    }
                }

                // Handle changed documents
                foreach (var documentId in projectChange.GetChangedDocuments())
                {
                    var newDocument = newSolution.GetDocument(documentId);
                    var filePath = newDocument.FilePath;

                    ModifiedFileResponse modifiedFileResponse;
                    if (!filePathToResponseMap.TryGetValue(filePath, out modifiedFileResponse))
                    {
                        modifiedFileResponse = new ModifiedFileResponse(filePath);
                        filePathToResponseMap[filePath] = modifiedFileResponse;
                    }

                    if (wantTextChanges)
                    {
                        var originalDocument = oldSolution.GetDocument(documentId);
                        var textChanges = await newDocument.GetTextChangesAsync(originalDocument);
                        var linePositionSpanTextChanges = await LinePositionSpanTextChange.Convert(originalDocument, textChanges);

                        modifiedFileResponse.Changes = modifiedFileResponse.Changes != null
                            ? modifiedFileResponse.Changes.Union(linePositionSpanTextChanges)
                            : linePositionSpanTextChanges;
                    }
                    else
                    {
                        var text = await newDocument.GetTextAsync();
                        modifiedFileResponse.Buffer = text.ToString();
                    }
                }
            }

            return filePathToResponseMap.Values;
        }
    }
}
