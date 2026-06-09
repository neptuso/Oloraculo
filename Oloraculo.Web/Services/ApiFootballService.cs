using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Oloraculo.Web.DAL;
using Oloraculo.Web.Helpers;
using Oloraculo.Web.Models;
using Oloraculo.Web.Models.ApiFootballModels;

namespace Oloraculo.Web.Services
{
    public class ApiFootballService
    {
        private readonly HttpClient _http;
        private readonly OloraculoDbContext _db;
        private readonly OloraculoConfig _config;
        private readonly AvailabilityNewsService _availability;
        private bool IsConfigured => !string.IsNullOrWhiteSpace(_config.ApiFootballApiKey);
        public ApiFootballService(HttpClient httpClient, OloraculoDbContext db, IOptions<OloraculoConfig> config, AvailabilityNewsService availability)
        {
            this._http = httpClient;
            this._db = db;
            this._config = config.Value;
            this._availability = availability;
        }

        public Task<ApiFootballRefreshReport> RefreshAsync(string fixtureId, CancellationToken ct = default) =>
            RefreshFixtureContextAsync(fixtureId, ct);

        public async Task<ApiFootballRefreshReport> RefreshFixtureContextAsync(string fixtureId, CancellationToken ct = default)
        {
            if (!IsConfigured)
                return new ApiFootballRefreshReport { IsConfigured = false, Notes = ["La clave de API-Football no está configurada."] };

            var errors = new List<string>();
            var notes = new List<string>();
            try
            {
                var fixture = await _db.Fixtures.FindAsync([fixtureId], ct);
                if (fixture is null)
                    return new ApiFootballRefreshReport { IsConfigured = true, Errors = [$"No se encontró el partido {fixtureId}."] };

                var mapping = await _db.ApiMappings.SingleOrDefaultAsync(m => m.LocalFixtureId == fixtureId, ct);
                if (mapping is null)
                {
                    var refresh = await RefreshFixturesAsync(ct);
                    mapping = await _db.ApiMappings.SingleOrDefaultAsync(m => m.LocalFixtureId == fixtureId, ct);
                    if (mapping is null)
                        return new ApiFootballRefreshReport { IsConfigured = true, Notes = refresh.Notes, Errors = ["No se encontró un mapeo de API para este partido local."] };
                }

                var coverage = await GetApiAsync<ApiLeagueResponse>(
                    $"leagues?id={_config.ApiFootballLeagueId}&season={_config.ApiFootballSeason}",
                    "cobertura",
                    errors,
                    ct);
                var coverageInfo = coverage?.Response.FirstOrDefault()?.League.Coverage;
                if (coverageInfo is not null)
                    notes.Add($"La cobertura indica lesiones={coverageInfo.Injuries}, cuotas={coverageInfo.Odds}, alineaciones={coverageInfo.Fixtures.Lineups}.");

                var fixtureInjuries = await GetApiAsync<ApiInjuryResponse>(
                    $"injuries?fixture={mapping.ExternalFixtureId}",
                    "lesiones del partido",
                    errors,
                    ct);
                var leagueInjuries = await GetApiAsync<ApiInjuryResponse>(
                    $"injuries?league={_config.ApiFootballLeagueId}&season={_config.ApiFootballSeason}",
                    "lesiones de la liga",
                    errors,
                    ct);
                var lineups = await GetApiAsync<ApiLineupResponse>(
                    $"fixtures/lineups?fixture={mapping.ExternalFixtureId}",
                    "alineaciones",
                    errors,
                    ct);
                var preMatchOdds = await GetApiAsync<ApiOddsResponse>(
                    $"odds?fixture={mapping.ExternalFixtureId}",
                    "cuotas previas",
                    errors,
                    ct);
                var liveOdds = await GetApiAsync<ApiOddsResponse>(
                    $"odds/live?fixture={mapping.ExternalFixtureId}",
                    "cuotas en vivo",
                    errors,
                    ct);

                var fixtureInjuryRows = fixtureInjuries?.Response.Count ?? 0;
                var leagueInjuryRows = leagueInjuries?.Response.Count ?? 0;
                var lineupRows = lineups?.Response.Count ?? 0;
                var preMatchOddsRows = preMatchOdds?.Response.Count ?? 0;
                var liveOddsRows = liveOdds?.Response.Count ?? 0;

                var relevantInjuries = MergeRelevantInjuries(fixture, fixtureInjuries?.Response ?? [], leagueInjuries?.Response ?? []);
                var unavailablePlayers = new HashSet<string>(StringComparer.Ordinal);
                var unavailableRoles = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var injury in relevantInjuries)
                {
                    var teamId = TeamNameNormalizer.ToId(injury.Team.Name);
                    var playerKey = AvailabilityNewsService.NormalizePlayerKey(injury.Player.Name);
                    var key = $"{teamId}|{playerKey}";
                    unavailablePlayers.Add(key);
                    unavailableRoles[key] = "Unknown";
                }

                var newsClaims = await _availability.AffectingClaimsForTeamsAsync([fixture.HomeTeamId, fixture.AwayTeamId], ct);
                foreach (var claim in newsClaims)
                {
                    var key = $"{claim.TeamId}|{claim.PlayerKey}";
                    unavailablePlayers.Add(key);
                    unavailableRoles[key] = claim.Position;
                }

                var homeUnavailable = unavailablePlayers.Count(k => k.StartsWith(fixture.HomeTeamId + "|", StringComparison.Ordinal));
                var awayUnavailable = unavailablePlayers.Count(k => k.StartsWith(fixture.AwayTeamId + "|", StringComparison.Ordinal));
                var homeImpacts = AvailabilityNewsService.SumImpacts(unavailableRoles.Where(p => p.Key.StartsWith(fixture.HomeTeamId + "|", StringComparison.Ordinal)).Select(p => p.Value));
                var awayImpacts = AvailabilityNewsService.SumImpacts(unavailableRoles.Where(p => p.Key.StartsWith(fixture.AwayTeamId + "|", StringComparison.Ordinal)).Select(p => p.Value));
                var context = await _db.FixtureContexts.FindAsync([fixtureId], ct);
                if (context is null)
                {
                    context = new FixtureContext { FixtureId = fixtureId };
                    _db.FixtureContexts.Add(context);
                }

                context.UnavailableHomePlayers = homeUnavailable;
                context.UnavailableAwayPlayers = awayUnavailable;
                context.UnavailableHomeAttackImpact = homeImpacts.Attack;
                context.UnavailableHomeDefenseImpact = homeImpacts.Defense;
                context.UnavailableAwayAttackImpact = awayImpacts.Attack;
                context.UnavailableAwayDefenseImpact = awayImpacts.Defense;
                context.HasLineups = lineupRows > 0;
                context.HasOdds = preMatchOddsRows > 0 || liveOddsRows > 0;
                context.HasAvailabilityNews = newsClaims.Count > 0;
                context.Notes = $"Actualizado desde API-Football. lesiones del partido={fixtureInjuryRows}; lesiones de la liga={leagueInjuryRows}; noticias confirmadas={newsClaims.Count}; roles matcheados={newsClaims.Count(c => c.Position != "Unknown")}; roles desconocidos={newsClaims.Count(c => c.Position == "Unknown")}; alineaciones={lineupRows}; cuotas previas={preMatchOddsRows}; cuotas en vivo={liveOddsRows}.";
                context.UpdatedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);

                notes.Add($"Filas de lesiones del partido: {fixtureInjuryRows}. Filas de lesiones de liga/temporada: {leagueInjuryRows}. Bajas o dudas relevantes guardadas: equipo A {homeUnavailable}, equipo B {awayUnavailable}.");
                if (newsClaims.Count > 0)
                    notes.Add($"Noticias confirmadas incluidas en el contexto: {newsClaims.Count}.");
                notes.Add($"Filas de alineaciones: {lineupRows}. Filas de cuotas previas: {preMatchOddsRows}. Filas de cuotas en vivo: {liveOddsRows}.");
                if (fixtureInjuryRows == 0 && leagueInjuryRows == 0)
                    notes.Add("No llegaron filas de lesiones. API-Football puede soportar lesiones para la competencia, pero todavía no tener bajas asociadas.");
                if (preMatchOddsRows == 0)
                    notes.Add("No llegaron cuotas previas. API-Football documenta las cuotas previas como limitadas a los últimos 7 días.");
                if (liveOddsRows == 0)
                    notes.Add("No llegaron cuotas en vivo. Es esperable salvo que el partido esté cerca de empezar, en vivo o recién terminado.");

                return new ApiFootballRefreshReport
                {
                    IsConfigured = true,
                    ContextRows = homeUnavailable + awayUnavailable,
                    FixtureInjuryRows = fixtureInjuryRows,
                    LeagueInjuryRows = leagueInjuryRows,
                    LineupRows = lineupRows,
                    PreMatchOddsRows = preMatchOddsRows,
                    LiveOddsRows = liveOddsRows,
                    Notes = notes,
                    Errors = errors
                };
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
                return new ApiFootballRefreshReport { IsConfigured = true, Errors = errors };
            }
        }
        public async Task<ApiFootballRefreshReport> RefreshFixturesAsync(CancellationToken ct = default)
        {
            if (!IsConfigured)
                return new ApiFootballRefreshReport { IsConfigured = false, Notes = ["La clave de API-Football no está configurada. Los datos CSV siguen funcionando."] };

            var errors = new List<string>();
            var notes = new List<string>();
            try
            {
                var response = await _http.GetFromJsonAsync<ApiFixtureResponse>(
                    $"fixtures?league={_config.ApiFootballLeagueId}&season={_config.ApiFootballSeason}&timezone=UTC", ct);
                var items = response?.Response ?? [];
                var local = await _db.Fixtures.ToListAsync(ct);
                var byPair = local.ToDictionary(f => PairKey(f.HomeTeamId, f.AwayTeamId));
                var matched = 0;

                foreach (var api in items)
                {
                    var home = TeamNameNormalizer.ToId(api.Teams.Home.Name);
                    var away = TeamNameNormalizer.ToId(api.Teams.Away.Name);
                    if (!byPair.TryGetValue(PairKey(home, away), out var fixture))
                        continue;

                    fixture.KickoffUtc = api.Fixture.Date;
                    fixture.Venue = api.Fixture.Venue?.Name;
                    fixture.City = api.Fixture.Venue?.City;
                    fixture.Status = api.Fixture.Status?.Short;
                    fixture.Source = "API-Football";
                    matched++;

                    var existing = await _db.ApiMappings.SingleOrDefaultAsync(m => m.LocalFixtureId == fixture.Id, ct);
                    if (existing is null)
                        _db.ApiMappings.Add(new ApiMapping { LocalFixtureId = fixture.Id, ExternalFixtureId = api.Fixture.Id.ToString() });
                    else
                        existing.ExternalFixtureId = api.Fixture.Id.ToString();
                }

                await _db.SaveChangesAsync(ct);
                notes.Add($"Se obtuvieron {items.Count} filas de partidos y se matchearon {matched} partidos locales de fase de grupos.");
                return new ApiFootballRefreshReport { IsConfigured = true, FixturesFetched = items.Count, FixturesMatched = matched, Notes = notes };
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
                return new ApiFootballRefreshReport { IsConfigured = true, Errors = errors };
            }
        }

        public async Task<AvailabilityRefreshReport> EnrichAvailabilityRolesAsync(CancellationToken ct = default)
        {
            if (!IsConfigured)
                return new AvailabilityRefreshReport { IsConfigured = false, Notes = ["La clave de API-Football no está configurada. No se pueden resolver roles."] };

            var errors = new List<string>();
            var notes = new List<string>();
            var claims = await _db.AvailabilityClaims
                .Where(c => c.Status != AvailabilityClaimStatus.Available && c.Status != AvailabilityClaimStatus.NotRelevant)
                .ToListAsync(ct);

            if (claims.Count == 0)
                return new AvailabilityRefreshReport { IsConfigured = true, Notes = ["No hay reclamos de disponibilidad para enriquecer con roles."] };

            var apiTeams = await GetApiAsync<ApiTeamListResponse>(
                $"teams?league={_config.ApiFootballLeagueId}&season={_config.ApiFootballSeason}",
                "equipos API-Football",
                errors,
                ct);
            var teamMap = (apiTeams?.Response ?? [])
                .GroupBy(t => TeamNameNormalizer.ToId(t.Team.Name), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Team.Id, StringComparer.Ordinal);
            var matched = 0;
            var unknown = 0;

            foreach (var teamClaims in claims.GroupBy(c => c.TeamId))
            {
                if (!teamMap.TryGetValue(teamClaims.Key, out var apiTeamId))
                {
                    foreach (var claim in teamClaims)
                        MarkUnknown(claim);
                    unknown += teamClaims.Count();
                    continue;
                }

                var squad = await GetApiAsync<ApiSquadResponse>($"players/squads?team={apiTeamId}", $"plantel {teamClaims.Key}", errors, ct);
                var squadCandidates = (squad?.Response.FirstOrDefault()?.Players ?? [])
                    .Select(p => new PlayerRoleCandidate(p.Id, p.Name, p.Position, "players/squads"))
                    .ToList();
                List<PlayerRoleCandidate>? fallbackCandidates = null;

                foreach (var claim in teamClaims)
                {
                    var role = MatchPlayerRole(claim.Player, squadCandidates);
                    if (role is null)
                    {
                        fallbackCandidates ??= await FallbackPlayerCandidatesAsync(apiTeamId, errors, ct);
                        role = MatchPlayerRole(claim.Player, fallbackCandidates);
                    }

                    if (role is null)
                    {
                        MarkUnknown(claim);
                        unknown++;
                    }
                    else
                    {
                        claim.ApiFootballPlayerId = role.Id;
                        claim.Position = AvailabilityNewsService.NormalizePosition(role.Position);
                        claim.PositionSource = role.Source;
                        claim.PositionMatchedAt = DateTimeOffset.UtcNow;
                        matched++;
                    }
                }
            }

            await _db.SaveChangesAsync(ct);
            var contexts = 0;
            foreach (var fixture in await _db.Fixtures.AsNoTracking().Select(f => f.Id).ToListAsync(ct))
            {
                if (await _availability.RefreshFixtureContextCountsAsync(fixture, [], ct))
                    contexts++;
            }

            notes.Add($"Roles API-Football resueltos: {matched}. Roles desconocidos: {unknown}.");
            return new AvailabilityRefreshReport
            {
                IsConfigured = true,
                RoleMatchedClaims = matched,
                RoleUnknownClaims = unknown,
                ContextRowsUpdated = contexts,
                Notes = notes,
                Errors = errors
            };
        }

        private async Task<T?> GetApiAsync<T>(string uri, string label, List<string> errors, CancellationToken ct)
        {
            try
            {
                return await _http.GetFromJsonAsync<T>(uri, ct);
            }
            catch (Exception ex)
            {
                errors.Add($"{label}: {ex.Message}");
                return default;
            }
        }

        private async Task<List<PlayerRoleCandidate>> FallbackPlayerCandidatesAsync(long apiTeamId, List<string> errors, CancellationToken ct)
        {
            var response = await GetApiAsync<ApiPlayerStatsResponse>($"players?team={apiTeamId}&season={_config.ApiFootballSeason}", $"jugadores {apiTeamId}", errors, ct);
            return (response?.Response ?? [])
                .Select(row => new PlayerRoleCandidate(
                    row.Player.Id,
                    row.Player.Name,
                    row.Statistics.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Games.Position))?.Games.Position ?? "",
                    $"players season {_config.ApiFootballSeason}"))
                .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                .ToList();
        }

        private static void MarkUnknown(AvailabilityClaim claim)
        {
            claim.ApiFootballPlayerId = null;
            claim.Position = "Unknown";
            claim.PositionSource = "Unknown";
            claim.PositionMatchedAt = DateTimeOffset.UtcNow;
        }

        public static PlayerRoleCandidate? MatchPlayerRole(string playerName, IEnumerable<PlayerRoleCandidate> candidates)
        {
            var byFullName = candidates
                .GroupBy(c => AvailabilityNewsService.NormalizePlayerKey(c.Name), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var fullKey = AvailabilityNewsService.NormalizePlayerKey(playerName);
            if (byFullName.TryGetValue(fullKey, out var exact) && exact.Count == 1)
                return exact[0];

            var byInitialLast = candidates
                .GroupBy(c => InitialLastKey(c.Name), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var initialLast = InitialLastKey(playerName);
            if (byInitialLast.TryGetValue(initialLast, out var loose) && loose.Count == 1)
                return loose[0];

            return null;
        }

        public static string InitialLastKey(string playerName)
        {
            var parts = AvailabilityNewsService.NormalizePlayerKey(playerName)
                .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || parts[0].Length == 0)
                return string.Join("-", parts);

            return $"{parts[0][0]}-{parts[^1]}";
        }

        private static string PairKey(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";
        private static IReadOnlyList<ApiInjury> MergeRelevantInjuries(Fixture fixture, IEnumerable<ApiInjury> fixtureInjuries, IEnumerable<ApiInjury> leagueInjuries)
        {
            var relevant = new Dictionary<string, ApiInjury>();
            foreach (var injury in fixtureInjuries.Concat(leagueInjuries))
            {
                var teamId = TeamNameNormalizer.ToId(injury.Team.Name);
                if (teamId != fixture.HomeTeamId && teamId != fixture.AwayTeamId)
                    continue;

                var playerKey = injury.Player.Id > 0 ? injury.Player.Id.ToString() : injury.Player.Name;
                var key = $"{teamId}|{playerKey}|{injury.Player.Type}|{injury.Player.Reason}";
                relevant.TryAdd(key, injury);
            }

            return relevant.Values.ToList();
        }

    }

    public sealed record PlayerRoleCandidate(long Id, string Name, string Position, string Source);
}
