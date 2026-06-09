namespace Oloraculo.Web.Models
{
    public sealed record TournamentSnapshotSummary(
        int Id,
        DateTimeOffset CreatedAt,
        string ModelName,
        string InputSummaryHash,
        int? Simulations,
        string? Error)
    {
        public bool IsValid => string.IsNullOrWhiteSpace(Error);
    }
}
