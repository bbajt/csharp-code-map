namespace CodeMap.Roslyn.Extraction;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using VbSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax;
using CodeMapRefKind = CodeMap.Core.Enums.RefKind;

/// <summary>
/// Classifies a syntax node into a RefKind, or returns null if it's not a trackable reference.
/// </summary>
internal static class RefKindClassifier
{
    /// <summary>
    /// Attempts to classify the given node as a reference.
    /// Returns (targetSymbol, refKind) or null if not applicable.
    /// </summary>
    public static (ISymbol Target, CodeMapRefKind Kind)? TryClassify(SyntaxNode node, SemanticModel model)
    {
        // CALL: method invocation
        if (node is InvocationExpressionSyntax invocation)
        {
            var symbol = model.GetSymbolInfo(invocation).Symbol;
            if (symbol is IMethodSymbol method)
                return (UnwrapMethod(method), CodeMapRefKind.Call);
            return null;
        }

        // INSTANTIATE: object creation
        if (node is ObjectCreationExpressionSyntax creation)
        {
            var symbol = model.GetSymbolInfo(creation).Symbol;
            if (symbol is IMethodSymbol ctor)
                return (ctor.ContainingType.OriginalDefinition, CodeMapRefKind.Instantiate);
            return null;
        }

        // INSTANTIATE: implicit object creation (new())
        if (node is ImplicitObjectCreationExpressionSyntax implicitCreation)
        {
            var symbol = model.GetSymbolInfo(implicitCreation).Symbol;
            if (symbol is IMethodSymbol ctor)
                return (ctor.ContainingType.OriginalDefinition, CodeMapRefKind.Instantiate);
            return null;
        }

        // WRITE: assignment LHS
        if (node is AssignmentExpressionSyntax assignment)
        {
            var leftSymbol = model.GetSymbolInfo(assignment.Left).Symbol;
            if (leftSymbol is IPropertySymbol or IFieldSymbol)
                return (leftSymbol, CodeMapRefKind.Write);
            return null;
        }

        // READ: explicit member access (property/field/event read via dot notation)
        if (node is MemberAccessExpressionSyntax memberAccess)
        {
            // Skip: LHS of assignment (Write handles it)
            if (memberAccess.Parent is AssignmentExpressionSyntax assignExpr &&
                assignExpr.Left == memberAccess)
                return null;

            var symbol = model.GetSymbolInfo(memberAccess).Symbol;
            if (symbol is IPropertySymbol or IFieldSymbol or IEventSymbol)
                return (symbol, CodeMapRefKind.Read);
            return null;
        }

        // READ: simple identifier (implicit-this access or local field access)
        if (node is IdentifierNameSyntax identifier)
        {
            // Skip: it's the Name side of a member access — handled above
            if (identifier.Parent is MemberAccessExpressionSyntax)
                return null;

            // Skip: it's the Name (or Alias) side of a global:: alias-qualified name
            if (identifier.Parent is AliasQualifiedNameSyntax)
                return null;

            // Skip: LHS of assignment (Write handles it)
            if (identifier.Parent is AssignmentExpressionSyntax assignExpr2 &&
                assignExpr2.Left == identifier)
                return null;

            var symbol = model.GetSymbolInfo(identifier).Symbol;
            if (symbol is IPropertySymbol or IFieldSymbol or IEventSymbol)
                return (symbol, CodeMapRefKind.Read);
            return null;
        }

        // --- VB.NET branches ---

        // VB.NET CALL: method invocation
        if (node is VbSyntax.InvocationExpressionSyntax vbInvocation)
        {
            var symbol = model.GetSymbolInfo(vbInvocation).Symbol;
            if (symbol is IMethodSymbol method)
                return (UnwrapMethod(method), CodeMapRefKind.Call);
            return null;
        }

        // VB.NET INSTANTIATE: object creation (New Foo(...))
        if (node is VbSyntax.ObjectCreationExpressionSyntax vbCreation)
        {
            var symbol = model.GetSymbolInfo(vbCreation).Symbol;
            if (symbol is IMethodSymbol ctor)
                return (ctor.ContainingType.OriginalDefinition, CodeMapRefKind.Instantiate);
            return null;
        }

        // VB.NET WRITE: assignment LHS
        if (node is VbSyntax.AssignmentStatementSyntax vbAssignment)
        {
            var leftSymbol = model.GetSymbolInfo(vbAssignment.Left).Symbol;
            if (leftSymbol is IPropertySymbol or IFieldSymbol)
                return (leftSymbol, CodeMapRefKind.Write);
            return null;
        }

        // VB.NET READ: explicit member access
        if (node is VbSyntax.MemberAccessExpressionSyntax vbMemberAccess)
        {
            if (vbMemberAccess.Parent is VbSyntax.AssignmentStatementSyntax vbAssignExpr &&
                vbAssignExpr.Left == vbMemberAccess)
                return null;

            var symbol = model.GetSymbolInfo(vbMemberAccess).Symbol;
            if (symbol is IPropertySymbol or IFieldSymbol or IEventSymbol)
                return (symbol, CodeMapRefKind.Read);
            return null;
        }

        // VB.NET READ: simple identifier
        if (node is VbSyntax.IdentifierNameSyntax vbIdentifier)
        {
            if (vbIdentifier.Parent is VbSyntax.MemberAccessExpressionSyntax)
                return null;
            if (vbIdentifier.Parent is VbSyntax.AssignmentStatementSyntax vbAssignExpr2 &&
                vbAssignExpr2.Left == vbIdentifier)
                return null;

            var symbol = model.GetSymbolInfo(vbIdentifier).Symbol;
            if (symbol is IPropertySymbol or IFieldSymbol or IEventSymbol)
                return (symbol, CodeMapRefKind.Read);
            return null;
        }

        return null;
    }

    /// <summary>
    /// Normalize an IMethodSymbol to the form stored by the baseline indexer.
    /// Reduced extension methods (receiver.Ext()) and closed generics (Foo&lt;int&gt;())
    /// resolve to the same declaration via OriginalDefinition, so callers map to the
    /// stored symbol_id of the static/open generic declaration.
    /// </summary>
    private static IMethodSymbol UnwrapMethod(IMethodSymbol method)
    {
        // ReducedFrom: `receiver.Ext()` → original static method (with `this` parameter).
        // Must come before OriginalDefinition: a reduced-extension's OriginalDefinition
        // is itself still a reduced-extension form.
        if (method.ReducedFrom is { } reducedFrom)
            method = reducedFrom;

        // OriginalDefinition: `Foo<int>()` → `Foo<T>()`. No-op for non-generic methods.
        return (IMethodSymbol)method.OriginalDefinition;
    }
}
