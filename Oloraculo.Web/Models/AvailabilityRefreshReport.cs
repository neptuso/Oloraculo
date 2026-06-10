namespace Oloraculo.Web.Models
{
    public class AvailabilityRefreshReport
    {
        public bool IsConfigured { get; init; }
        public int SourcesFetched { get; init; }
        public int SourcesSkipped { get; init; }
        public int ClaimsSaved { get; init; }
        public int ConfirmedOutClaims { get; init; }
        public int DoubtfulClaims { get; init; }
        public int AvailableClaims { get; init; }
        public int ClaimsAffectingPredictions { get; init; }
        public int RoleMatchedClaims { get; init; }
        public int RoleUnknownClaims { get; init; }
        public int ContextRowsUpdated { get; init; }
        public IReadOnlyList<string> Notes { get; init; } = [];
        public IReadOnlyList<string> Errors { get; init; } = [];
    }
}
