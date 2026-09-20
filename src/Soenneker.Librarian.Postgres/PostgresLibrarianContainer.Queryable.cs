using Soenneker.Librarian.Abstractions.Serialization;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;

namespace Soenneker.Librarian.Postgres;

public sealed partial class PostgresLibrarianContainer
{
    private readonly ConcurrentDictionary<Type, object> _queryRoots = new();

    public IQueryable<T> BuildQueryable<T>()
    {
        _ = LibrarianJson.Contract(typeof(T));
        Check();
        return (IQueryable<T>)_queryRoots.GetOrAdd(typeof(T), _ => new PostgresQueryable<T>(new PostgresQueryProvider<T>(this)));
    }

    internal async ValueTask<object?> ExecuteQuery(PostgresQueryPlan plan, CancellationToken cancellationToken = default)
    {
        Check();
        foreach (string path in plan.Paths) await EnsureIndex(path, cancellationToken).NoSync();
        await using NpgsqlConnection connection = await _database.Open(cancellationToken).NoSync();
        await using NpgsqlCommand command = Command(connection, "");
        string Parameter(object value)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
            return "$" + command.Parameters.Count;
        }
        string Filter(PostgresQueryFilter filter) => filter.Operation switch
        {
            "all" => "TRUE",
            "none" => "FALSE",
            "and" => "(" + Filter(filter.Left!) + " AND " + Filter(filter.Right!) + ")",
            "or" => "(" + Filter(filter.Left!) + " OR " + Filter(filter.Right!) + ")",
            "not" => "(NOT " + Filter(filter.Left!) + ")",
            "computed" => "COALESCE((" + filter.ScalarLeft!.Sql(Parameter, "d") + " " + filter.Comparison + " " + filter.ScalarRight!.Sql(Parameter, "d") + "),FALSE)",
            "term" or "in" or "prefix" or "pattern" or "regex" => IndexedFilter(filter),
            _ => throw new NotSupportedException("Unknown query filter.")
        };
        string IndexedFilter(PostgresQueryFilter filter)
        {
            string path = Parameter(filter.Path!);
            string match = filter.Operation switch
            {
                "in" => "v.value=ANY(" + Parameter(filter.Values!) + ")",
                "prefix" => "v.value>=" + Parameter(filter.Value!) + " AND v.value<" + Parameter(filter.Value! + "G"),
                "pattern" => "v.value LIKE " + Parameter(filter.Value!),
                "regex" => "v.value ~ " + Parameter(filter.Value!),
                _ => "v.value" + filter.Comparison + Parameter(filter.Value!)
            };
            return "EXISTS(SELECT 1 FROM public.librarian_postgres_values v WHERE v.database_key=d.database_key AND v.container=d.container " +
                "AND v.id_key=d.id_key AND v.path=" + path + " AND " + match + ")";
        }

        string page;
        string pageOrder;
        if (plan.Stages.Count > 0)
        {
            string? previous = null;
            foreach (PostgresQueryStage stage in plan.Stages.Append(plan.CurrentStage))
            {
                var sorting = new List<string>();
                string joins = previous is null ? "" : " JOIN (" + previous + ") prior ON prior.id_key=d.id_key";
                for (var i = 0; i < stage.Orders.Count; i++)
                {
                    (string path, bool descending) = stage.Orders[i];
                    string alias = "ordering" + i;
                    joins += " JOIN public.librarian_postgres_values " + alias + " ON " + alias + ".database_key=d.database_key AND " + alias + ".container=d.container " +
                        "AND " + alias + ".id_key=d.id_key AND " + alias + ".path=" + Parameter(path);
                    sorting.Add(alias + ".value" + (descending ? " DESC" : " ASC"));
                }
                if (previous is not null) sorting.Add("prior.sequence");
                else sorting.Add("d.id_key" + (stage.Orders.Count > 0 && stage.Orders[^1].Descending ? " DESC" : " ASC"));
                string order = string.Join(",", sorting);
                previous = "SELECT d.id_key,row_number() OVER (ORDER BY " + order + ") AS sequence FROM public.librarian_postgres_documents d" + joins +
                    " WHERE d.database_key=$1 AND d.container=$2 AND " + Filter(stage.Filter) +
                    " ORDER BY " + order + " LIMIT " + Parameter(stage.Take) + " OFFSET " + Parameter(stage.Skip);
                if (stage.Distinct is { } distinct)
                {
                    string key = distinct.Sql(Parameter, "d");
                    if (distinct.Type.IsValueType && Nullable.GetUnderlyingType(distinct.Type) is null)
                        key = "COALESCE(" + key + (distinct.Type == typeof(bool) ? ",FALSE)" : ",0)");
                    previous = "SELECT id_key,sequence FROM (SELECT candidates.id_key,candidates.sequence,row_number() OVER (PARTITION BY " + key +
                        " ORDER BY candidates.sequence) AS duplicate FROM (" + previous + ") candidates JOIN public.librarian_postgres_documents d " +
                        "ON d.database_key=$1 AND d.container=$2 AND d.id_key=candidates.id_key) deduplicated WHERE duplicate=1";
                }
            }
            page = previous!;
            pageOrder = "page.sequence";
        }
        else
        {
            string predicate = Filter(plan.Filter);
            var join = "";
            var sorting = new List<string>();
            var pageSorting = new List<string>();
            var sortColumns = new List<string>();
            for (var i = 0; i < plan.Orders.Count; i++)
            {
                (string path, bool descending) = plan.Orders[i];
                string alias = "ordering" + i;
                join += " JOIN public.librarian_postgres_values " + alias + " ON " + alias + ".database_key=d.database_key AND " + alias + ".container=d.container " +
                    "AND " + alias + ".id_key=d.id_key AND " + alias + ".path=" + Parameter(path);
                string direction = descending ? " DESC" : " ASC";
                sorting.Add(alias + ".value" + direction);
                pageSorting.Add("page.sort" + i + direction);
                sortColumns.Add(alias + ".value AS sort" + i);
            }
            string idDirection = plan.Orders.Count > 0 && plan.Orders[^1].Descending ? " DESC" : " ASC";
            sorting.Add("d.id_key" + idDirection);
            pageSorting.Add("page.id_key" + idDirection);
            string order = string.Join(",", sorting);
            string columns = plan.CountOnly ? "1" : "d.id_key" + (sortColumns.Count == 0 ? "" : "," + string.Join(",", sortColumns));
            bool needsOrder = !plan.CountOnly && (!plan.Aggregate || plan.Skip != 0 || plan.Take != long.MaxValue);
            page = "SELECT " + columns + " FROM public.librarian_postgres_documents d" + join +
                " WHERE d.database_key=$1 AND d.container=$2 AND " + predicate +
                (needsOrder ? " ORDER BY " + order : "") + " LIMIT " + Parameter(plan.Take) + " OFFSET " + Parameter(plan.Skip);
            pageOrder = string.Join(",", pageSorting);
        }
        if (plan.Aggregate)
        {
            string function = plan.Terminal switch { nameof(Queryable.Average) => "AVG", nameof(Queryable.Sum) => "SUM", nameof(Queryable.Min) => "MIN", _ => "MAX" };
            string value = plan.AggregateExpression!.Sql(Parameter, "d");
            if (Nullable.GetUnderlyingType(plan.AggregateType!) is null) value = "COALESCE(" + value + ",0)";
            if (plan.Terminal == nameof(Queryable.Average) && (Nullable.GetUnderlyingType(plan.AggregateType!) ?? plan.AggregateType) == typeof(decimal))
                value = "(" + value + ")::numeric(57,28)";
            command.CommandText = "SELECT " + function + "(" + value + ") FROM (" + page + ") page JOIN public.librarian_postgres_documents d " +
                "ON d.database_key=$1 AND d.container=$2 AND d.id_key=page.id_key";
            object? result = await command.ExecuteScalarAsync(cancellationToken).NoSync();
            return result is DBNull ? null : result;
        }
        if (plan.CountOnly)
        {
            command.CommandText = "SELECT COUNT(*) FROM (" + page + ") page";
            return (long)(await command.ExecuteScalarAsync(cancellationToken).NoSync())!;
        }

        string selected = plan.Projection is null ? "documents.document" : string.Join(",", plan.Projection.Expressions.Select(column =>
            column.Operation == "path" ? "(documents.body #> " + Parameter(column.Path!.Split('.')) + ")::text" :
            "to_jsonb(" + column.Sql(Parameter, "documents") + ")::text"));
        command.CommandText = "SELECT " + selected + " FROM (" + page + ") page " +
            "JOIN public.librarian_postgres_documents documents ON documents.database_key=$1 AND documents.container=$2 AND documents.id_key=page.id_key ORDER BY " + pageOrder;
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).NoSync();
        if (plan.Projection is not null)
        {
            var rows = new List<string?[]>();
            while (await reader.ReadAsync(cancellationToken).NoSync())
            {
                var row = new string?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetString(i);
                rows.Add(row);
            }
            return rows;
        }
        var documents = new List<string>();
        while (await reader.ReadAsync(cancellationToken).NoSync()) documents.Add(reader.GetString(0));
        return documents;
    }
}
