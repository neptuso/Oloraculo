using Microsoft.EntityFrameworkCore;
using Oloraculo.Web.DAL;
using Oloraculo.Web.Helpers;
using Oloraculo.Web.Models;
using System.Text.Json;

namespace Oloraculo.Web.Services
{
    public class SnapshotService
    {
        private readonly OloraculoDbContext _db;
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = false,
            PropertyNameCaseInsensitive = true
        };

        public SnapshotService(OloraculoDbContext db) => _db = db;

        public async Task<PredictionSnapshot> SaveMatchAsync(MatchPrediction prediction, CancellationToken ct = default)
        {
            var snapshot = CreateMatchSnapshot(prediction);
            _db.Snapshots.Add(snapshot);
            await _db.SaveChangesAsync(ct);
            return snapshot;
        }

        public async Task<IReadOnlyList<PredictionSnapshot>> SaveMatchesAsync(IEnumerable<MatchPrediction> predictions, CancellationToken ct = default)
        {
            var snapshots = predictions.Select(CreateMatchSnapshot).ToList();
            _db.Snapshots.AddRange(snapshots);
            await _db.SaveChangesAsync(ct);
            return snapshots;
        }

        private static PredictionSnapshot CreateMatchSnapshot(MatchPrediction prediction)
        {
            var now = DateTimeOffset.UtcNow;
            var payload = JsonSerializer.Serialize(new
            {
                prediction.PredictorName,
                Outcome = prediction.Outcome,
                prediction.ExpectedHomeGoals,
                prediction.ExpectedAwayGoals,
                MostLikelyScore = prediction.MostLikelyScore is null ? null : $"{prediction.MostLikelyScore.Value.Home}-{prediction.MostLikelyScore.Value.Away}",
                prediction.FeaturesUsed,
                prediction.FeaturesMissing,
                Sources = prediction.Sources.Select(s => s.ToString())
            }, JsonOptions);

            return new PredictionSnapshot
            {
                Kind = "match",
                FixtureId = prediction.FixtureId,
                ModelName = prediction.PredictorName,
                CreatedAt = now,
                InputSummaryHash = CryptoUtil.GetSha256($"{prediction.FixtureId}|{now:yyyyMMddHH}"),
                PayloadJson = payload,
                Explanation = prediction.Explanation,
                HomeWin = prediction.Outcome.HomeWin,
                Draw = prediction.Outcome.Draw,
                AwayWin = prediction.Outcome.AwayWin
            };
        }

        public async Task<PredictionSnapshot> SaveTournamentAsync(TournamentProjection projection, CancellationToken ct = default)
        {
            var payload = JsonSerializer.Serialize(projection, JsonOptions);
            var snapshot = new PredictionSnapshot
            {
                Kind = "tournament",
                ModelName = projection.ModelName,
                CreatedAt = projection.GeneratedAt,
                InputSummaryHash = projection.InputSummaryHash,
                PayloadJson = payload,
                Explanation = $"{projection.Simulations:N0} simulaciones usando {projection.ModelName}.",
                HomeWin = 0,
                Draw = 0,
                AwayWin = 0
            };
            _db.Snapshots.Add(snapshot);
            await _db.SaveChangesAsync(ct);
            return snapshot;
        }

        public async Task<IReadOnlyList<TournamentSnapshotSummary>> TournamentSnapshotsAsync(int? take = null, CancellationToken ct = default)
        {
            var snapshots = await _db.Snapshots
                .AsNoTracking()
                .Where(s => s.Kind == "tournament")
                .ToListAsync(ct);

            IEnumerable<PredictionSnapshot> ordered = snapshots
                .OrderByDescending(s => s.CreatedAt)
                .ThenByDescending(s => s.Id);

            if (take is > 0)
                ordered = ordered.Take(take.Value);

            return ordered.Select(ToTournamentSummary).ToList();
        }

        public async Task<TournamentSnapshotLoadResult> LoadTournamentSnapshotAsync(int id, CancellationToken ct = default)
        {
            var snapshot = await _db.Snapshots
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Kind == "tournament" && s.Id == id, ct);

            if (snapshot is null)
                return new TournamentSnapshotLoadResult(null, "No se encontró el snapshot de torneo.");

            var projection = DeserializeTournamentProjection(snapshot.PayloadJson, out var error);
            if (projection is null)
                return new TournamentSnapshotLoadResult(null, error ?? "No se pudo leer el snapshot de torneo.");

            return new TournamentSnapshotLoadResult(projection, null);
        }

        private static TournamentSnapshotSummary ToTournamentSummary(PredictionSnapshot snapshot)
        {
            var projection = DeserializeTournamentProjection(snapshot.PayloadJson, out var error);
            return new TournamentSnapshotSummary(
                snapshot.Id,
                snapshot.CreatedAt,
                snapshot.ModelName,
                snapshot.InputSummaryHash,
                projection?.Simulations,
                error);
        }

        private static TournamentProjection? DeserializeTournamentProjection(string payloadJson, out string? error)
        {
            error = null;

            try
            {
                var projection = JsonSerializer.Deserialize<TournamentProjection>(payloadJson, JsonOptions);
                if (projection is null)
                {
                    error = "El snapshot no contiene una proyección de torneo.";
                    return null;
                }

                return projection;
            }
            catch (JsonException)
            {
                error = "El snapshot guardado no tiene un formato válido.";
                return null;
            }
        }
    }
}
