using System.Collections.Generic;

namespace Soenneker.Librarian.Postgres;

internal sealed record PostgresQueryStage(PostgresQueryFilter Filter, IReadOnlyList<(string Path, bool Descending)> Orders,
    long Skip, long Take, PostgresScalar? Distinct = null);
