namespace Oloraculo.Web.Models
{
    public class AvailabilityClaim
    {
        public int Id { get; set; }
        public required string Player { get; set; }
        public required string PlayerKey { get; set; }
        public required string TeamId { get; set; }
        public required string TeamName { get; set; }
        public AvailabilityClaimStatus Status { get; set; }
        public string Reason { get; set; } = "";
        public string Confidence { get; set; } = "";
        public AvailabilityEvidenceLevel EvidenceLevel { get; set; }
        public required string SourceUrl { get; set; }
        public string? Publisher { get; set; }
        public string SupportingQuote { get; set; } = "";
        public DateTimeOffset? ObservedDate { get; set; }
        public bool AffectsPrediction { get; set; }
        public long? ApiFootballPlayerId { get; set; }
        public string Position { get; set; } = "Unknown";
        public string PositionSource { get; set; } = "Unknown";
        public DateTimeOffset? PositionMatchedAt { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
