using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Soenneker.Librarian.Generators;

internal static class MongoRegistrationEmitter
{
    private static readonly DiagnosticDescriptor Unsupported = new("LIBGEN003",
        "Mongo serializer needs explicit registration",
        "Librarian cannot generate a Mongo serializer for '{0}'; register its factory explicitly", "Librarian",
        DiagnosticSeverity.Warning, true);

    internal static string Emit(IEnumerable<INamedTypeSymbol> roots, SourceProductionContext output)
    {
        var text = new StringBuilder(
            "internal static void RegisterMongo(global::Soenneker.Librarian.Mongo.MongoJsonSerializerRegistry registry)\n{\n");
        var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var root in roots)
            if (LibrarianGenerator.Accessible(root) && !root.IsGenericType)
                Visit(root);
        text.Append("}\n");
        return text.ToString();

        void Visit(ITypeSymbol type)
        {
            if (!visited.Add(type))
                return;
            string name = LibrarianGenerator.Name(type.WithNullableAnnotation(NullableAnnotation.NotAnnotated));
            if (!LibrarianGenerator.Accessible(type))
            {
                Reject(type);
                return;
            }

            if (type is IArrayTypeSymbol { Rank: 1 } array)
            {
                Visit(array.ElementType);
                Factory(type, "ArraySerializer<" + LibrarianGenerator.Name(array.ElementType) + ">",
                    Resolve(array.ElementType));
                return;
            }

            if (type is not INamedTypeSymbol named)
            {
                Reject(type);
                return;
            }

            string definition = named.OriginalDefinition.ToDisplayString();
            if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            {
                Visit(named.TypeArguments[0]);
                Factory(type, "NullableSerializer<" + LibrarianGenerator.Name(named.TypeArguments[0]) + ">",
                    Resolve(named.TypeArguments[0]));
                return;
            }

            if (definition == "System.Collections.Generic.List<T>")
            {
                Visit(named.TypeArguments[0]);
                Factory(type,
                    "EnumerableInterfaceImplementerSerializer<" + name + ", " +
                    LibrarianGenerator.Name(named.TypeArguments[0]) + ">", Resolve(named.TypeArguments[0]));
                return;
            }

            if (definition == "System.Collections.Generic.Dictionary<TKey, TValue>" &&
                named.TypeArguments[0].SpecialType == SpecialType.System_String)
            {
                foreach (var argument in named.TypeArguments)
                    Visit(argument);
                Factory(type,
                    "DictionaryInterfaceImplementerSerializer<" + name + ", " +
                    string.Join(", ", named.TypeArguments.Select(LibrarianGenerator.Name)) + ">",
                    "global::MongoDB.Bson.Serialization.Options.DictionaryRepresentation.Document, " +
                    string.Join(", ", named.TypeArguments.Select(Resolve)));
                return;
            }

            if (named.TypeKind == TypeKind.Enum ||
                named.SpecialType is >= SpecialType.System_Boolean and <= SpecialType.System_Double || name is "string"
                    or "global::System.String" or "global::System.Guid" or "global::System.DateTime"
                    or "global::System.DateTimeOffset" or "global::System.DateOnly" or "global::System.TimeOnly"
                    or "global::System.TimeSpan" or "global::System.Text.Json.JsonElement")
            {
                text.Append("registry.RegisterScalar<").Append(name).Append(">();\n");
                return;
            }

            if (named.SpecialType != SpecialType.None || named.IsGenericType ||
                named.TypeKind is not (TypeKind.Class or TypeKind.Struct) || named.ContainingNamespace.ToDisplayString()
                    .StartsWith("System", System.StringComparison.Ordinal))
            {
                Reject(type);
                return;
            }

            var members = new List<(string Name, string Json)>();
            var seen = new HashSet<string>();
            for (INamedTypeSymbol? current = named; current is not null; current = current.BaseType)
                foreach (ISymbol member in current.GetMembers())
                {
                    if (member.IsStatic || !seen.Add(member.Name))
                        continue;
                    ITypeSymbol? memberType = member switch
                    {
                        IPropertySymbol
                        {
                            IsIndexer: false, GetMethod.DeclaredAccessibility: Accessibility.Public
                        } p => p.Type,
                        IFieldSymbol
                        {
                            DeclaredAccessibility: Accessibility.Public, IsImplicitlyDeclared: false
                        } f => f.Type,
                        _ => null
                    };
                    if (memberType is null)
                        continue;
                    if (member.GetAttributes().Any(a =>
                            a.AttributeClass?.ToDisplayString() ==
                            "System.Text.Json.Serialization.JsonIgnoreAttribute" && a.NamedArguments.Length == 0))
                        continue;
                    Visit(memberType);
                    string? json = member.GetAttributes()
                                         .FirstOrDefault(a =>
                                             a.AttributeClass?.ToDisplayString() ==
                                             "System.Text.Json.Serialization.JsonPropertyNameAttribute")
                                         ?.ConstructorArguments.FirstOrDefault().Value as string;
                    members.Add((member.Name,
                        json is null
                            ? "global::System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(" +
                              LibrarianGenerator.Literal(member.Name) + ")"
                            : LibrarianGenerator.Literal(json)));
                }

            text.Append("registry.RegisterDocument<").Append(name)
                .Append(">(new global::System.Collections.Generic.Dictionary<string, string> {\n");
            foreach (var member in members)
                text.Append("[").Append(LibrarianGenerator.Literal(member.Name)).Append("] = ").Append(member.Json)
                    .Append(",\n");
            text.Append("});\n");
        }

        void Factory(ITypeSymbol type, string serializer, string arguments) => text.Append("registry.Register<")
            .Append(LibrarianGenerator.Name(type))
            .Append(">(options => new global::MongoDB.Bson.Serialization.Serializers.").Append(serializer).Append('(')
            .Append(arguments).Append("));\n");

        string Resolve(ITypeSymbol type) => "(global::MongoDB.Bson.Serialization.IBsonSerializer<" +
                                            LibrarianGenerator.Name(type) + ">)registry.Create(typeof(" +
                                            LibrarianGenerator.Name(
                                                type.WithNullableAnnotation(NullableAnnotation.NotAnnotated)) +
                                            "), options)";

        void Reject(ITypeSymbol type) => output.ReportDiagnostic(Diagnostic.Create(Unsupported,
            type.Locations.FirstOrDefault(l => l.IsInSource), type.ToDisplayString()));
    }
}
