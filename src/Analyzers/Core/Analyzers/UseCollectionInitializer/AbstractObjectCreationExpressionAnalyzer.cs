﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis.LanguageService;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;

namespace Microsoft.CodeAnalysis.UseCollectionInitializer
{
    internal abstract class AbstractObjectCreationExpressionAnalyzer<
        TExpressionSyntax,
        TStatementSyntax,
        TObjectCreationExpressionSyntax,
        TVariableDeclaratorSyntax,
        TMatch>
        where TExpressionSyntax : SyntaxNode
        where TStatementSyntax : SyntaxNode
        where TObjectCreationExpressionSyntax : TExpressionSyntax
        where TVariableDeclaratorSyntax : SyntaxNode
    {
        protected UpdateExpressionState<TExpressionSyntax, TStatementSyntax> State;

        protected TObjectCreationExpressionSyntax _objectCreationExpression;
        protected bool _analyzeForCollectionExpression;

        protected ISyntaxFacts SyntaxFacts => this.State.SyntaxFacts;
        protected SemanticModel SemanticModel => this.State.SemanticModel;

        protected abstract bool ShouldAnalyze(CancellationToken cancellationToken);
        protected abstract bool TryAddMatches(ArrayBuilder<TMatch> matches, CancellationToken cancellationToken);

        public void Initialize(
            UpdateExpressionState<TExpressionSyntax, TStatementSyntax> state,
            TObjectCreationExpressionSyntax objectCreationExpression,
            bool analyzeForCollectionExpression)
        {
            State = state;
            _objectCreationExpression = objectCreationExpression;
            _analyzeForCollectionExpression = analyzeForCollectionExpression;
        }

        protected void Clear()
        {
            State = default;
            _objectCreationExpression = null;
            _analyzeForCollectionExpression = false;
        }

        protected ImmutableArray<TMatch> AnalyzeWorker(CancellationToken cancellationToken)
        {
            if (!ShouldAnalyze(cancellationToken))
                return default;

            using var _ = ArrayBuilder<TMatch>.GetInstance(out var matches);
            if (!TryAddMatches(matches, cancellationToken))
                return default;

            return matches.ToImmutable();
        }

        protected UpdateExpressionState<TExpressionSyntax, TStatementSyntax>? TryInitializeVariableDeclarationCase(
            TExpressionSyntax rootExpression,
            TStatementSyntax containingStatement,
            CancellationToken cancellationToken)
        {
            if (!this.SyntaxFacts.IsLocalDeclarationStatement(containingStatement))
                return null;

            var containingDeclarator = rootExpression.Parent?.Parent;
            if (containingDeclarator is null)
                return null;

            var initializedSymbol = this.SemanticModel.GetDeclaredSymbol(containingDeclarator, cancellationToken);
            if (initializedSymbol is ILocalSymbol local &&
                local.Type is IDynamicTypeSymbol)
            {
                // Not supported if we're creating a dynamic local.  The object we're instantiating
                // may not have the members that we're trying to access on the dynamic object.
                return null;
            }

            if (!this.SyntaxFacts.IsDeclaratorOfLocalDeclarationStatement(containingDeclarator, containingStatement))
                return null;

            var valuePattern = this.SyntaxFacts.GetIdentifierOfVariableDeclarator(containingDeclarator);
            return new(this.SemanticModel, this.SyntaxFacts, rootExpression, valuePattern, initializedSymbol);
        }

        protected UpdateExpressionState<TExpressionSyntax, TStatementSyntax>? TryInitializeAssignmentCase(
            TExpressionSyntax rootExpression,
            TStatementSyntax containingStatement,
            CancellationToken cancellationToken)
        {
            if (!this.SyntaxFacts.IsSimpleAssignmentStatement(containingStatement))
                return null;

            this.SyntaxFacts.GetPartsOfAssignmentStatement(containingStatement,
                out var left, out var right);
            if (right != rootExpression)
                return null;

            var typeInfo = this.SemanticModel.GetTypeInfo(left, cancellationToken);
            if (typeInfo.Type is IDynamicTypeSymbol || typeInfo.ConvertedType is IDynamicTypeSymbol)
            {
                // Not supported if we're initializing something dynamic.  The object we're instantiating
                // may not have the members that we're trying to access on the dynamic object.
                return null;
            }

            var initializedSymbol = this.SemanticModel.GetSymbolInfo(left, cancellationToken).GetAnySymbol();
            return new(this.SemanticModel, this.SyntaxFacts, rootExpression, left, initializedSymbol);
        }
    }
}
