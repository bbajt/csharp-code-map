namespace CodeMap.Roslyn.Extraction;

using System.Xml;
using Microsoft.CodeAnalysis;

/// <summary>
/// Extracts plain-text summary from Roslyn XML documentation comment strings.
/// </summary>
internal static class DocumentationReader
{
    public static string? GetSummary(ISymbol symbol)
    {
        string? xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml))
            return null;

        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);

            var summaryNode = doc.SelectSingleNode("//summary");
            if (summaryNode is null)
                return null;

            // Normalize whitespace in the extracted text
            string text = summaryNode.InnerText;
            return NormalizeWhitespace(text);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static string NormalizeWhitespace(string text)
    {
        // Collapse internal whitespace, trim leading/trailing
        var parts = text.Split((char[])['\r', '\n', '\t', ' '],
            StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts).Trim();
    }
}
