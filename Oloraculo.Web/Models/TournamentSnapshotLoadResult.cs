namespace Oloraculo.Web.Models
{
    public sealed record TournamentSnapshotLoadResult(TournamentProjection? Projection, string? Error)
    {
        public bool IsValid => Projection is not null && string.IsNullOrWhiteSpace(Error);
    }
}
