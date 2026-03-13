namespace CodeMap.Roslyn.Extraction;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
                return (method, CodeMapRefKind.Call);
            return null;
        }

        // INSTANTIATE: object creation
        if (node is ObjectCreationExpressionSyntax creation)
        {
            var symbol = model.GetSymbolInfo(creation).Symbol;
            if (symbol is IMethodSymbol ctor)
                return (ctor.ContainingType, CodeMapRefKind.Instantiate);
            return null;
        }

        // INSTANTIATE: implicit object creation (new())
        if (node is ImplicitObjectCreationExpressionSyntax implicitCreation)
        {
            var symbol = model.GetSymbolInfo(implicitCreation).Symbol;
            if (symbol is IMethodSymbol ctor)
                return (ctor.ContainingType, CodeMapRefKind.Instantiate);
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

            // Skip: LHS of assignment (Write handles it)
            if (identifier.Parent is AssignmentExpressionSyntax assignExpr2 &&
                assignExpr2.Left == identifier)
                return null;

            var symbol = model.GetSymbolInfo(identifier).Symbol;
            if (symbol is IPropertySymbol or IFieldSymbol or IEventSymbol)
                return (symbol, CodeMapRefKind.Read);
            return null;
        }

        return null;
    }
}
