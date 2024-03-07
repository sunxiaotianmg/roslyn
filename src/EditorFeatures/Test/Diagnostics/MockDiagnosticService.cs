﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Host.Mef;
using Xunit;

namespace Microsoft.CodeAnalysis.Editor.UnitTests.Diagnostics
{
    [Export(typeof(IDiagnosticService)), Shared, PartNotDiscoverable]
    internal class MockDiagnosticService : IDiagnosticService
    {
        public const string DiagnosticId = "MockId";

        private DiagnosticData? _diagnosticData;

        public event EventHandler<ImmutableArray<DiagnosticsUpdatedArgs>>? DiagnosticsUpdated;

        [ImportingConstructor]
        [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
        public MockDiagnosticService()
        {
        }

        internal void CreateDiagnosticAndFireEvents(Workspace workspace, MockDiagnosticAnalyzerService analyzerService, Location location, DiagnosticKind diagnosticKind, bool isSuppressed)
        {
            var document = workspace.CurrentSolution.Projects.Single().Documents.Single();
            _diagnosticData = DiagnosticData.Create(Diagnostic.Create(DiagnosticId, "MockCategory", "MockMessage", DiagnosticSeverity.Error, DiagnosticSeverity.Error, isEnabledByDefault: true, warningLevel: 0, isSuppressed: isSuppressed,
                location: location),
                document);

            analyzerService.AddDiagnostic(_diagnosticData, diagnosticKind);
            DiagnosticsUpdated?.Invoke(this, ImmutableArray.Create(DiagnosticsUpdatedArgs.DiagnosticsCreated(
                this, workspace, workspace.CurrentSolution,
                GetProjectId(workspace), GetDocumentId(workspace),
                ImmutableArray.Create(_diagnosticData))));
        }

        private static DocumentId GetDocumentId(Workspace workspace)
            => workspace.CurrentSolution.Projects.Single().Documents.Single().Id;

        private static ProjectId GetProjectId(Workspace workspace)
            => workspace.CurrentSolution.Projects.Single().Id;
    }
}
