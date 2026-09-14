using Soenneker.Librarian.Abstractions;

internal static class PlannerBenchmarks
{
    internal static void Run(ILibrarianContainer container)
    {
        var root = container.BuildQueryable<Row>();
        for (int i = 0; i < 100; i++)
        {
            int start = i * 1000;
            string group = $"group-{start / 10:D6}";
            var page = root.Where(row => row.Score >= 0 && row.Group == group).OrderByDescending(row => row.Score).Skip(2).Take(3).ToArray();
            if (!page.Select(row => row.Score).SequenceEqual(new[] { start + 7, start + 6, start + 5 })) throw new Exception("Composite page mismatch");
            var residual = root.Where(row => row.Score >= 0 && row.Group == group && row.Score % 2 == 0).Take(3).ToArray();
            if (!residual.Select(row => row.Score).SequenceEqual(new[] { start, start + 2, start + 4 })) throw new Exception("Residual mismatch");
            if (root.Count(row => row.Score >= 0 && row.Group == group) != 10) throw new Exception("Composite count mismatch");
        }
        if (!root.Where(row => row.Score >= 0).Select(row => row.Score).Skip(90000).Take(10).ToArray().SequenceEqual(Enumerable.Range(90000,10)))
            throw new Exception("Projected page mismatch");
        Console.WriteLine("Operation,MedianMicroseconds,BytesPerOperation");
        string payload = new string('x', 128);
        if (root.Count(row => row.Payload == payload && row.Score >= 0) != 100000) throw new Exception("Broad count mismatch");
        if (!root.Where(row => row.Payload == payload && row.Score >= 0).OrderBy(row => row.Score).Take(3).ToArray().Select(row => row.Score)
            .SequenceEqual(new[] { 0, 1, 2 })) throw new Exception("Broad ordered page mismatch");
        AuditBenchmarks.Measure("Broad-multi-field-ordered-page-3", _ => root.Where(row => row.Payload == payload && row.Score >= 0).OrderBy(row => row.Score).Take(3).ToArray().Length, 2000);
        AuditBenchmarks.Measure("Multi-field-ordered-page-3", i => { string group = $"group-{i % 100 * 100:D6}"; return root.Where(row => row.Score >= 0 && row.Group == group).OrderByDescending(row => row.Score).Skip(2).Take(3).ToArray().Length; }, 2000);
        AuditBenchmarks.Measure("Filter-order-other-property-page-3", i => { string group = $"group-{i % 100 * 100:D6}"; return root.Where(row => row.Group == group).OrderByDescending(row => row.Score).Skip(2).Take(3).ToArray().Length; }, 2000);
        AuditBenchmarks.Measure("Multi-field-count", i => { string group = $"group-{i % 100 * 100:D6}"; return root.Count(row => row.Score >= 0 && row.Group == group); }, 2000);
        AuditBenchmarks.Measure("Indexed-residual-take-3", i => { string group = $"group-{i % 100 * 100:D6}"; return root.Where(row => row.Score >= 0 && row.Group == group && row.Score % 2 == 0).Take(3).ToArray().Length; }, 2000);
        AuditBenchmarks.Measure("Select-Skip-90000-Take-10", _ => root.Where(row => row.Score >= 0).Select(row => row.Score).Skip(90000).Take(10).ToArray().Length, 2000);
    }
}
