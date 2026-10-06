using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Svm.Analyzers;

/// <summary>Checks the declared project graph without loading or executing project files.</summary>
public static class ArchitectureGraph
{
    public static ImmutableArray<string> Validate(
        string root, string policyXml, IEnumerable<KeyValuePair<string, string>> projectFiles)
    {
        var errors = ImmutableArray.CreateBuilder<string>();
        try
        {
            var policy = XDocument.Parse(policyXml).Root
                ?? throw new InvalidDataException("Missing Architecture root.");
            var projects = policy.Elements("Project").ToDictionary(e => (string)e.Attribute("name")!, StringComparer.Ordinal);
            var inputs = projectFiles.ToDictionary(p => Path.GetFullPath(p.Key), p => p.Value, StringComparer.Ordinal);
            var knownPaths = projects.ToDictionary(p => FullPath(root, (string)p.Value.Attribute("path")!), p => p.Key, StringComparer.Ordinal);
            var graph = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            foreach (var path in inputs.Keys.Where(p => !knownPaths.ContainsKey(p)))
                errors.Add("Unlisted project: " + path);

            foreach (var entry in projects)
            {
                var name = entry.Key;
                var path = FullPath(root, (string)entry.Value.Attribute("path")!);
                graph[name] = new List<string>();
                if (!inputs.TryGetValue(path, out var text))
                {
                    errors.Add(name + ": project file missing from architecture inputs.");
                    continue;
                }

                var document = XDocument.Parse(text);
                var expected = Names(entry.Value.Elements("Reference"));
                var actual = new HashSet<string>(StringComparer.Ordinal);
                foreach (var reference in document.Descendants("ProjectReference"))
                {
                    var include = (string?)reference.Attribute("Include") ?? "";
                    if (include.Length == 0 || include.IndexOfAny(new[] { '$', '*', ';' }) >= 0 || reference.Attribute("Condition") != null)
                    {
                        errors.Add(name + ": project reference must be a literal unconditional path.");
                        continue;
                    }

                    var target = FullPath(Path.GetDirectoryName(path)!, include);
                    if (!knownPaths.TryGetValue(target, out var referencedName))
                    {
                        errors.Add(name + ": reference is outside the approved graph: " + include);
                        continue;
                    }
                    if (!actual.Add(referencedName)) errors.Add(name + ": duplicate reference to " + referencedName);
                    graph[name].Add(referencedName);
                    if (!expected.Contains(referencedName)) errors.Add(name + ": forbidden reference to " + referencedName);
                }

                foreach (var missing in expected.Except(actual)) errors.Add(name + ": missing approved reference to " + missing);
                var allowedPackages = Names(entry.Value.Elements("Package"));
                foreach (var package in document.Descendants("PackageReference"))
                {
                    var id = (string?)package.Attribute("Include") ?? "";
                    if (!allowedPackages.Contains(id)) errors.Add(name + ": forbidden direct package " + id);
                    if (package.Attribute("Version") != null || package.Element("Version") != null ||
                        package.Attribute("VersionOverride") != null || package.Element("VersionOverride") != null)
                        errors.Add(name + ": package versions belong to the central baseline: " + id);
                }

                if (document.Descendants("Reference").Any()) errors.Add(name + ": raw assembly references are not allowed.");
                if (document.Descendants("FrameworkReference").Any() &&
                    name != "Svm.Security" && name != "Svm.ServiceDefaults")
                    errors.Add(name + ": unapproved framework reference.");
            }

            var visiting = new HashSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in graph.Keys) Visit(name, graph, visiting, visited, errors);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException || ex is ArgumentException || ex is InvalidDataException)
        {
            errors.Add("Invalid architecture input: " + ex.Message);
        }
        return errors.ToImmutable();
    }

    private static HashSet<string> Names(IEnumerable<XElement> elements) =>
        new HashSet<string>(elements.Select(e => (string)e.Attribute("name")!), StringComparer.Ordinal);

    private static string FullPath(string root, string path) =>
        Path.GetFullPath(Path.Combine(root, path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));

    private static void Visit(string name, Dictionary<string, List<string>> graph, HashSet<string> visiting,
        HashSet<string> visited, ImmutableArray<string>.Builder errors)
    {
        if (visited.Contains(name)) return;
        if (!visiting.Add(name))
        {
            errors.Add("Circular project reference at " + name);
            return;
        }
        if (graph.TryGetValue(name, out var references))
            foreach (var reference in references) Visit(reference, graph, visiting, visited, errors);
        visiting.Remove(name);
        visited.Add(name);
    }
}
