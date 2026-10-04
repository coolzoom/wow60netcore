using System.Xml.Linq;

namespace FrameXml;

/// <summary>Element and attribute lookups ignore case and XML namespaces, like the client's XMLTree.</summary>
public static class XmlExtensions
{
    public static string? Attr(this XElement node, string name) =>
        node.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    public static XElement? Child(this XElement node, string name) =>
        node.Elements().FirstOrDefault(e => e.Is(name));

    public static IEnumerable<XElement> Children(this XElement node, string name) =>
        node.Elements().Where(e => e.Is(name));

    public static bool Is(this XElement node, string name) =>
        node.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);

    public static string Tag(this XElement node) => node.Name.LocalName;
}
