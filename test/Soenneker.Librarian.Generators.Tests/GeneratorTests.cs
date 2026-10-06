using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Soenneker.Librarian.Abstractions.Queries;

namespace Soenneker.Librarian.Generators.Tests;

public sealed class GeneratorTests
{
    [Test]
    public void Captured_locals_report_fallback_without_emitting_invalid_code()
    {
        var result = Generate("""
            using System.Linq;
            using Soenneker.Librarian.Abstractions.Queries;
            [GenerateLibrarianQueries]
            public class Queries {
                public IQueryable<int> Run(IQueryable<int> source, int minimum) => source.Where(x => x > minimum);
            }
            """);
        Check(result.Diagnostics.Any(d => d.Id == "LIBGEN002"));
        Check(!result.GeneratedTrees.Any(t => t.ToString().Contains("InterceptsLocation(")));
    }

    [Test]
    public void Supported_calls_generate_interceptors_and_preserve_escaped_members()
    {
        var result = Generate("""
            using System.Linq;
            using Soenneker.Librarian.Abstractions.Queries;
            [LibrarianModel] public sealed class Row { public int @event { get; set; } }
            [GenerateLibrarianQueries]
            public class Queries {
                public IQueryable<int> Run(IQueryable<Row> source) => source.Where(x => x.@event > 2).Select(x => x.@event);
            }
            """);
        Check(!result.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Warning));
        Check(result.GeneratedTrees.Any(t => t.ToString().Contains("GeneratedQueryFunctions.Register")));
        Check(result.GeneratedTrees.Any(t => t.ToString().Contains("value.@event")));
    }

    [Test]
    public void Inaccessible_models_and_anonymous_projections_report_diagnostics()
    {
        var result = Generate("""
            using System.Linq;
            using Soenneker.Librarian.Abstractions.Queries;
            [GenerateLibrarianQueries]
            public class Queries {
                [LibrarianModel] private sealed class Hidden { public int Age { get; set; } }
                public object Run(IQueryable<int> source) => source.Select(x => new { Age = x });
            }
            """);
        Check(result.Diagnostics.Any(d => d.Id == "LIBGEN001"));
        Check(result.Diagnostics.Any(d => d.Id == "LIBGEN002"));
    }

    [Test]
    public void Inherited_members_compile_and_private_setters_use_fallback()
    {
        var result = Generate("""
            using System.Linq;
            using Soenneker.Librarian.Abstractions.Queries;
            public class BaseRow { public int Age { get; set; } }
            [LibrarianModel] public sealed class Row : BaseRow { public string Name { get; set; } = ""; }
            [GenerateLibrarianQueries]
            public class Queries {
                public int Age { get; private set; }
                public IQueryable<Queries> Run(IQueryable<int> source) => source.Select(x => new Queries { Age = x });
            }
            """);
        Check(result.Diagnostics.Any(d => d.Id == "LIBGEN002"));
        Check(result.GeneratedTrees.Any(t => t.ToString().Contains("Register<global::BaseRow")));
    }

    [Test]
    public void Librarian_roots_and_branch_assignments_need_no_attributes()
    {
        var result = Generate("""
            using System.Linq;
            using Soenneker.Librarian.Abstractions;
            public sealed class Row { public int Age { get; set; } }
            public class Queries {
                public IQueryable<int> Run(ILibrarianContainer container, bool filter) {
                    var query = container.BuildQueryable<Row>();
                    if (filter) query = query.Where(x => x.Age >= 18);
                    var alias = query;
                    return alias.OrderBy(x => x.Age).Select(x => x.Age);
                }
            }
            """);
        Check(!result.Diagnostics.Any());
        string generated = string.Join("\n", result.GeneratedTrees.Select(t => t.ToString()));
        Check(generated.Contains("GeneratedQueryMetadata.Register<global::Row"));
        Check(generated.Split("GeneratedQueryFunctions.Register").Length - 1 == 3);
    }

    [Test]
    public void Awaited_repository_roots_and_static_named_linq_calls_are_discovered()
    {
        var result = Generate("""
            using System.Linq;
            using System.Threading.Tasks;
            using Soenneker.Librarian.Abstractions;
            using Soenneker.Documents.Document;
            public sealed class Row { public int Age { get; set; } }
            public class Queries {
                public async Task<IQueryable<Row>> Run(ILibrarianRepository<Document> repository) {
                    var query = await repository.BuildQueryable<Row>();
                    return Queryable.Where(predicate: x => x.Age >= 18, source: query);
                }
            }
            """);
        Check(!result.Diagnostics.Any());
        Check(result.GeneratedTrees.Any(t => t.ToString().Contains("GeneratedQueryFunctions.Register")));
    }

    [Test]
    public void Other_providers_mixed_locals_and_unknown_helpers_are_not_intercepted()
    {
        var result = Generate("""
            using System.Linq;
            using Soenneker.Librarian.Abstractions;
            public sealed class Row { public int Age { get; set; } }
            public sealed class OtherContainer { public IQueryable<Row> BuildQueryable<T>() => null!; }
            public class Queries {
                public IQueryable<Row> Other(OtherContainer container) => container.BuildQueryable<Row>().Where(x => x.Age > 1);
                public IQueryable<Row> Helper(IQueryable<Row> query) => query.Where(x => x.Age > 1);
                public IQueryable<Row> Mixed(ILibrarianContainer container, IQueryable<Row> other, bool condition) {
                    var query = container.BuildQueryable<Row>();
                    if (condition) query = other;
                    return query.Where(x => x.Age > 1);
                }
                public void Loop(ILibrarianContainer container, IQueryable<Row> other) {
                    var query = container.BuildQueryable<Row>();
                    for (int i = 0; i < 2; i++) {
                        var selected = query.Where(x => x.Age > 1);
                        query = other;
                    }
                }
            }
            """);
        Check(!result.Diagnostics.Any());
        Check(!result.GeneratedTrees.Any(t => t.ToString().Contains("InterceptsLocation(")));
    }

    [Test]
    public void Automatic_captures_report_information_and_keep_runtime_fallback()
    {
        var result = Generate("""
            using System.Linq;
            using Soenneker.Librarian.Abstractions;
            public class Queries {
                public IQueryable<int> Run(ILibrarianContainer container, int minimum) => container.BuildQueryable<int>().Where(x => x > minimum);
            }
            """);
        Check(result.Diagnostics.Any(d => d.Id == "LIBGEN002" && d.Severity == DiagnosticSeverity.Info));
        Check(!result.GeneratedTrees.Any(t => t.ToString().Contains("InterceptsLocation(")));
    }

    [Test]
    public void Ref_escapes_are_not_assumed_to_preserve_the_query_provider()
    {
        var result = Generate("""
            using System.Linq;
            using Soenneker.Librarian.Abstractions;
            public class Queries {
                public IQueryable<int> Run(ILibrarianContainer container, IQueryable<int> other) {
                    var query = container.BuildQueryable<int>();
                    ref var alias = ref query;
                    alias = other;
                    return query.Where(x => x > 1);
                }
                public IQueryable<int> Replace(ILibrarianContainer container) {
                    var query = container.BuildQueryable<int>();
                    Change(ref query);
                    return query.Where(x => x > 1);
                }
                private void Change(ref IQueryable<int> query) { }
            }
            """);
        Check(!result.Diagnostics.Any());
        Check(!result.GeneratedTrees.Any(t => t.ToString().Contains("InterceptsLocation(")));
    }

    [Test]
    public void Json_registration_also_discovers_models_without_query_calls()
    {
        var result = Generate("""
            using System.Text.Json.Serialization.Metadata;
            using Soenneker.Librarian.Abstractions.Serialization;
            public sealed class Row { public int Age { get; set; } }
            public class Startup {
                public void Register(JsonTypeInfo<Row> info) => LibrarianJson.Register<Row>(info);
            }
            """);
        Check(!result.Diagnostics.Any());
        Check(result.GeneratedTrees.Any(t => t.ToString().Contains("GeneratedQueryMetadata.Register<global::Row")));
    }

    private static GeneratorDriverRunResult Generate(string source)
    {
        var parse = new CSharpParseOptions(LanguageVersion.Preview).WithFeatures(
            new[] { new KeyValuePair<string, string>("InterceptorsNamespaces", "Soenneker.Librarian.Generated") });
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(LibrarianModelAttribute).Assembly.Location).Distinct(StringComparer.OrdinalIgnoreCase);
        var compilation = CSharpCompilation.Create("GeneratorTest", new[] { CSharpSyntaxTree.ParseText(source, parse, path: "Query.cs") },
            paths.Select(path => MetadataReference.CreateFromFile(path)), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { new LibrarianGenerator().AsSourceGenerator() }, parseOptions: parse);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var generated, out var diagnostics);
        var errors = generated.GetDiagnostics().Concat(diagnostics).Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors.Select(d => d.ToString())));
        return driver.GetRunResult();
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Generator assertion failed.");
    }
}
