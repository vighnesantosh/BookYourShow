using BookYourShow.Contracts;

namespace Catalog.Api;

/// <summary>Seat layout stored as JSONB on the hall and served as static JSON (S3 + CloudFront in production).</summary>
public sealed record SeatLayout(IReadOnlyList<LayoutRow> Rows)
{
    public IEnumerable<SeatDefinition> Seats() =>
        Rows.SelectMany(r => r.Seats.Select(s => new SeatDefinition($"{r.Row}{s.Number}", s.Category)));

    public IReadOnlySet<string> Categories() => Rows.SelectMany(r => r.Seats).Select(s => s.Category).ToHashSet();

    /// <summary>Convenience for admins: a rectangular hall with a category per row.</summary>
    public static SeatLayout Grid(GridSpec spec)
    {
        var rows = Enumerable.Range(0, spec.RowCount).Select(i =>
        {
            var row = ((char)('A' + i)).ToString();
            var category = spec.CategoryByRow?.GetValueOrDefault(row) ?? spec.DefaultCategory;
            var seats = Enumerable.Range(1, spec.SeatsPerRow).Select(n => new LayoutSeat(n, category, n, i)).ToList();
            return new LayoutRow(row, seats);
        }).ToList();
        return new SeatLayout(rows);
    }

    public string? Validate()
    {
        if (Rows.Count == 0) return "Layout has no rows.";
        var ids = Seats().Select(s => s.SeatId).ToList();
        if (ids.Count == 0) return "Layout has no seats.";
        if (ids.Count != ids.Distinct().Count()) return "Seat ids must be unique.";
        if (ids.Count > 100_000) return "Layout is too large.";
        return null;
    }
}

public sealed record LayoutRow(string Row, IReadOnlyList<LayoutSeat> Seats);

public sealed record LayoutSeat(int Number, string Category, int X, int Y);

public sealed record GridSpec(int RowCount, int SeatsPerRow, string DefaultCategory, Dictionary<string, string>? CategoryByRow)
{
    public string? Validate() =>
        RowCount is < 1 or > 26 ? "RowCount must be 1-26." :
        SeatsPerRow is < 1 or > 100 ? "SeatsPerRow must be 1-100." :
        string.IsNullOrWhiteSpace(DefaultCategory) ? "DefaultCategory is required." : null;
}
