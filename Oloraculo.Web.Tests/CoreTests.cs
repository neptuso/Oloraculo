using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Oloraculo.Web;
using Oloraculo.Web.DAL;
using Oloraculo.Web.Helpers;
using Oloraculo.Web.Models;
using Oloraculo.Web.Models.ApiFootballModels;
using Oloraculo.Web.Models.CsvModels;
using Oloraculo.Web.Predictors;
using Oloraculo.Web.Probability;
using Oloraculo.Web.Services;
using Oloraculo.Web.Services.Simulation;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Oloraculo.Web.Tests;

public class CoreTests
{
    [Fact]
    public void OutcomeProbabilities_NormalizesAndUsesOutcomeLabels()
    {
        var p = new OutcomeProbabilities(2, 1, 1).Normalize();

        Assert.True(p.IsValid);
        Assert.Equal(0.5, p.HomeWin, 3);
        Assert.Equal("Home", p.TopPick);
    }

    [Theory]
    [InlineData("Korea Republic", "south-korea")]
    [InlineData("Türkiye", "turkey")]
    [InlineData("USA", "united-states")]
    public void TeamNameNormalizer_HandlesAliases(string input, string expected)
    {
        Assert.Equal(expected, TeamNameNormalizer.ToId(input));
    }

    [Theory]
    [InlineData("argentina", "Argentina", "ar")]
    [InlineData("brazil", "Brazil", "br")]
    [InlineData("france", "France", "fr")]
    [InlineData("japan", "Japan", "jp")]
    [InlineData("united-states", "United States", "us")]
    [InlineData("south-korea", "South Korea", "kr")]
    [InlineData("turkey", "Turkey", "tr")]
    [InlineData("ivory-coast", "Ivory Coast", "ci")]
    [InlineData("congo-dr", "Congo DR", "cd")]
    [InlineData("curacao", "Curacao", "cw")]
    [InlineData("cape-verde", "Cape Verde", "cv")]
    [InlineData("england", "England", "gb-eng")]
    [InlineData("scotland", "Scotland", "gb-sct")]
    [InlineData("wales", "Wales", "gb-wls")]
    [InlineData("northern-ireland", "Northern Ireland", "gb-nir")]
    [InlineData("china-pr", "China PR", "cn")]
    [InlineData("chinese-taipei", "Chinese Taipei", "tw")]
    [InlineData("korea-dpr", "Korea DPR", "kp")]
    [InlineData("republic-of-ireland", "Republic of Ireland", "ie")]
    [InlineData("palestine", "Palestine", "ps")]
    [InlineData("faroe-islands", "Faroe Islands", "fo")]
    public void TeamFlagCatalog_ResolvesStandardAndFootballTeamNames(string id, string name, string expected)
    {
        Assert.Equal(expected, TeamFlagCatalog.CodeFor(id, name));
    }

    [Fact]
    public void TeamFlagCatalog_ReturnsNoFlagForUnknownTeams()
    {
        Assert.Null(TeamFlagCatalog.CodeFor("made-up-xi", "Made Up XI"));
    }

    [Fact]
    public void OutcomeFromExpectation_TreatsEqualMagnitudeGapsSymmetrically()
    {
        var strongerHome = ProbabilityHelper.OutcomeFromExpectation(.78, 400);
        var strongerAway = ProbabilityHelper.OutcomeFromExpectation(.22, -400);

        Assert.Equal(strongerHome.Draw, strongerAway.Draw, 6);
    }

    [Fact]
    public void PoissonScoreline_ProducesARealProbabilityGrid()
    {
        var dist = ProbabilityHelper.PoissonScoreline(2.2, .7);
        var sum = 0.0;
        for (var h = 0; h <= dist.MaxGoals; h++)
            for (var a = 0; a <= dist.MaxGoals; a++)
                sum += dist.Probability(h, a);

        Assert.Equal(1.0, sum, 6);
        Assert.True(dist.ToOutcome().HomeWin > dist.ToOutcome().AwayWin);
        Assert.NotEqual((0, 0), dist.MostLikelyScoreline());
    }

    [Fact]
    public void GoalModel_ProducesUsableScorelineWhenTeamsHaveEnoughHistory()
    {
        var model = new GoalModel(
        [
            Result("a", "b", 2, 0),
            Result("a", "b", 1, 0),
            Result("b", "a", 1, 2)
        ]);

        var prediction = model.Predict(TestContext());

        Assert.False(prediction.Degraded);
        Assert.NotNull(prediction.Scoreline);
        Assert.True(prediction.ExpectedHomeGoals > 0.1);
        Assert.True(prediction.Outcome.IsValid);
    }

    [Fact]
    public void ContextModel_DoesNotClaimLineupsOrOddsWereUsedWithoutConversionLogic()
    {
        var goal = new GoalModel(
        [
            Result("a", "b", 2, 0),
            Result("a", "b", 1, 0),
            Result("b", "a", 1, 2)
        ]);
        var context = TestContext(fixtureContext: new FixtureContext
        {
            FixtureId = "test",
            HasLineups = true,
            HasOdds = true
        });

        var prediction = new GoalPlusRecentContextModel(goal).Predict(context);

        Assert.DoesNotContain(nameof(FeaturesEnum.Lineups), prediction.FeaturesUsed);
        Assert.DoesNotContain(nameof(FeaturesEnum.Odds), prediction.FeaturesUsed);
        Assert.Contains("modelo de impacto de alineaciones", prediction.FeaturesMissing);
        Assert.Contains("calibración por cuotas", prediction.FeaturesMissing);
        Assert.True(prediction.Degraded);
    }

    [Fact]
    public void ContextModel_BecomesUsableWhenAvailabilityActuallyAdjustsGoals()
    {
        var goal = new GoalModel(
        [
            Result("a", "b", 2, 0),
            Result("a", "b", 1, 0),
            Result("b", "a", 1, 2)
        ]);
        var context = TestContext(fixtureContext: new FixtureContext
        {
            FixtureId = "test",
            UnavailableHomePlayers = 2
        });

        var prediction = new GoalPlusRecentContextModel(goal).Predict(context);

        Assert.False(prediction.Degraded);
        Assert.Contains("Disponibilidad de jugadores", prediction.FeaturesUsed);
    }

    [Fact]
    public void FinalSelector_ChoosesHighestUsableRungWithoutAveraging()
    {
        var form = Prediction(3, "Forma reciente", .05, .05, .90);
        var goal = Prediction(4, "Goal", .90, .05, .05, scoreline: ProbabilityHelper.PoissonScoreline(3.0, .4));
        var context = Prediction(5, "Context", .10, .80, .10, degraded: true, missing: ["availability"]);

        var final = FinalPredictionSelector.Select([form, goal, context]);

        Assert.Equal("Oráculo final", final.PredictorName);
        Assert.Equal(4, final.PredictorPriority);
        Assert.Equal(goal.Outcome, final.Outcome);
        Assert.NotEqual(.475, final.Outcome.HomeWin, 3);
    }

    [Fact]
    public void FinalSelector_AppliesLightRankingBiasWhenEloAndFifaAgreeAgainstSelected()
    {
        var fifa = Prediction(1, "Ranking FIFA", .15, .20, .65, sources: [SourceMetadata.FifaRankings]);
        var elo = Prediction(2, "Elo", .10, .20, .70, sources: [SourceMetadata.EloRatings]);
        var goalScoreline = ProbabilityHelper.PoissonScoreline(1.4, 1.1);
        var goal = Prediction(4, "Goal", .45, .35, .20, scoreline: goalScoreline);

        var final = FinalPredictionSelector.Select([fifa, elo, goal]);

        Assert.Equal("Oráculo final", final.PredictorName);
        Assert.Equal(4, final.PredictorPriority);
        Assert.Equal(.40125, final.Outcome.HomeWin, 5);
        Assert.Equal(.3275, final.Outcome.Draw, 5);
        Assert.Equal(.27125, final.Outcome.AwayWin, 5);
        Assert.Same(goalScoreline, final.Scoreline);
        Assert.Contains(final.Drivers, d => d.Contains("calibración Elo/FIFA"));
        Assert.Contains("calibración Elo/FIFA", final.Explanation);
        Assert.Contains(SourceMetadata.FifaRankings, final.Sources);
        Assert.Contains(SourceMetadata.EloRatings, final.Sources);
    }

    [Fact]
    public void FinalSelector_DoesNotApplyRankingBiasWhenRankingModelsDisagree()
    {
        var fifa = Prediction(1, "Ranking FIFA", .65, .20, .15, sources: [SourceMetadata.FifaRankings]);
        var elo = Prediction(2, "Elo", .10, .20, .70, sources: [SourceMetadata.EloRatings]);
        var goal = Prediction(4, "Goal", .45, .35, .20);

        var final = FinalPredictionSelector.Select([fifa, elo, goal]);

        Assert.Equal(goal.Outcome, final.Outcome);
        Assert.DoesNotContain(final.Drivers, d => d.Contains("calibración Elo/FIFA"));
        Assert.DoesNotContain(SourceMetadata.FifaRankings, final.Sources);
        Assert.DoesNotContain(SourceMetadata.EloRatings, final.Sources);
    }

    [Fact]
    public void FinalSelector_DoesNotApplyRankingBiasWhenRankingModelIsDegraded()
    {
        var fifa = Prediction(1, "Ranking FIFA", .15, .20, .65, degraded: true, sources: [SourceMetadata.FifaRankings]);
        var elo = Prediction(2, "Elo", .10, .20, .70, sources: [SourceMetadata.EloRatings]);
        var goal = Prediction(4, "Goal", .45, .35, .20);

        var final = FinalPredictionSelector.Select([fifa, elo, goal]);

        Assert.Equal(goal.Outcome, final.Outcome);
        Assert.DoesNotContain(final.Drivers, d => d.Contains("calibración Elo/FIFA"));
        Assert.DoesNotContain(SourceMetadata.FifaRankings, final.Sources);
        Assert.DoesNotContain(SourceMetadata.EloRatings, final.Sources);
    }

    [Fact]
    public void RankingRefresh_ParsesFifaLuaRows()
    {
        var rows = RankingRefreshService.ParseFifaRankings(SampleFifaRaw());

        Assert.Equal(2, rows.Count);
        Assert.Equal("France", rows[0].Team);
        Assert.Equal("1877.32", rows[0].Points);
        Assert.Equal("2026-04-01", rows[0].RankingDate);
    }

    [Fact]
    public void RankingRefresh_ParsesEloHtmlRowsAndCleansImageText()
    {
        var date = new DateOnly(2026, 6, 5);
        var rows = RankingRefreshService.ParseEloRankings(SampleEloHtml(), date, "https://example.test/elo");

        Assert.Equal(2, rows.Count);
        Assert.Equal("Spain", rows[0].Team);
        Assert.Equal("2155", rows[0].Elo);
        Assert.Equal("2026-06-05", rows[0].RatingDate);
    }

    [Fact]
    public async Task RankingRefresh_WalksBackToLatestAvailableEloDateAndWritesParseableCsvs()
    {
        var root = NewTempRoot();
        try
        {
            var options = Options.Create(new OloraculoConfig
            {
                FifaRankingsRawUrl = "https://example.test/fifa",
                EloRankingsBaseUrl = "https://example.test/elo",
                EloRefreshMaxLookbackDays = 3
            });
            var handler = new FakeHttpMessageHandler(new Dictionary<string, string>
            {
                ["https://example.test/fifa"] = SampleFifaRaw(),
                ["https://example.test/elo?day=09&month=06&year=2026"] = "no rankings today",
                ["https://example.test/elo?day=08&month=06&year=2026"] = "still no rankings",
                ["https://example.test/elo?day=07&month=06&year=2026"] = SampleEloHtml()
            });
            var service = new RankingRefreshService(new HttpClient(handler), new TestEnvironment(root), options);

            var report = await service.RefreshAsync(new DateOnly(2026, 6, 9));

            Assert.True(report.AnyFileUpdated);
            Assert.Equal(new DateOnly(2026, 6, 7), report.EloRatingDate);
            Assert.Equal(2, CsvParsingHelper.ReadCsv<FifaCsvRow>(Path.Combine(root, "Data", OloraculoDataFiles.FifaRankingsCsv)).Count);
            Assert.Equal(2, CsvParsingHelper.ReadCsv<EloCsvRow>(Path.Combine(root, "Data", OloraculoDataFiles.EloCsv)).Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RankingRefresh_DoesNotOverwriteExistingCsvsWhenSourcesCannotParse()
    {
        var root = NewTempRoot();
        try
        {
            var data = Path.Combine(root, "Data");
            Directory.CreateDirectory(data);
            var fifaPath = Path.Combine(data, OloraculoDataFiles.FifaRankingsCsv);
            var eloPath = Path.Combine(data, OloraculoDataFiles.EloCsv);
            await File.WriteAllTextAsync(fifaPath, "existing fifa");
            await File.WriteAllTextAsync(eloPath, "existing elo");

            var options = Options.Create(new OloraculoConfig
            {
                FifaRankingsRawUrl = "https://example.test/fifa",
                EloRankingsBaseUrl = "https://example.test/elo",
                EloRefreshMaxLookbackDays = 0
            });
            var handler = new FakeHttpMessageHandler(new Dictionary<string, string>
            {
                ["https://example.test/fifa"] = "not lua",
                ["https://example.test/elo?day=09&month=06&year=2026"] = "not elo"
            });
            var service = new RankingRefreshService(new HttpClient(handler), new TestEnvironment(root), options);

            var report = await service.RefreshAsync(new DateOnly(2026, 6, 9));

            Assert.False(report.AnyFileUpdated);
            Assert.Equal("existing fifa", await File.ReadAllTextAsync(fifaPath));
            Assert.Equal("existing elo", await File.ReadAllTextAsync(eloPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AvailabilityNews_ParsesStructuredClaims()
    {
        var claims = AvailabilityNewsService.ParseClaimsFromJson("""
            {
              "claims": [
                {
                  "player": "Example Star",
                  "team": "France",
                  "status": "ConfirmedOutInjury",
                  "reason": "knee injury",
                  "confidence": "high",
                  "evidenceLevel": "Official",
                  "supportingText": "France confirmed Example Star will miss the World Cup.",
                  "sourceUrl": "https://ignored.test",
                  "publishedOrObservedDate": "2026-06-09"
                }
              ]
            }
            """, "https://example.test/source", "example.test");

        var claim = Assert.Single(claims);
        Assert.Equal("Example Star", claim.Player);
        Assert.Equal("france", claim.TeamId);
        Assert.Equal(AvailabilityClaimStatus.ConfirmedOutInjury, claim.Status);
        Assert.Equal(AvailabilityEvidenceLevel.Official, claim.EvidenceLevel);
        Assert.Equal("https://example.test/source", claim.SourceUrl);
    }

    [Fact]
    public void AvailabilityNews_RejectsMalformedJson()
    {
        Assert.ThrowsAny<JsonException>(() => AvailabilityNewsService.ParseClaimsFromJson("not json", "https://example.test"));
    }

    [Fact]
    public void AvailabilityNews_DoesNotPromoteSoftFitnessLanguage()
    {
        var claims = AvailabilityNewsService.ParseClaimsFromJson("""
            {"claims":[{"player":"Careful Wording","team":"Argentina","status":"FitnessConcern","reason":"race to be fit","confidence":"medium","evidenceLevel":"ReportedUncertain","supportingText":"could miss","sourceUrl":"","publishedOrObservedDate":""}]}
            """, "https://example.test/source", "example.test");

        AvailabilityNewsService.ApplyPredictionFlags(claims, requireCrossCheck: true);

        Assert.False(Assert.Single(claims).AffectsPrediction);
    }

    [Fact]
    public void AvailabilityNews_CuratedReputableOutClaimsAffectPredictions()
    {
        var singleReputable = new AvailabilityClaim
        {
            Player = "One Source",
            PlayerKey = AvailabilityNewsService.NormalizePlayerKey("One Source"),
            TeamId = "france",
            TeamName = "France",
            Status = AvailabilityClaimStatus.ConfirmedOutInjury,
            EvidenceLevel = AvailabilityEvidenceLevel.ReputableReported,
            SourceUrl = "https://one.test",
            Publisher = "one.test"
        };
        var official = new AvailabilityClaim
        {
            Player = "Official Player",
            PlayerKey = AvailabilityNewsService.NormalizePlayerKey("Official Player"),
            TeamId = "france",
            TeamName = "France",
            Status = AvailabilityClaimStatus.ConfirmedOutInjury,
            EvidenceLevel = AvailabilityEvidenceLevel.Official,
            SourceUrl = "https://federation.test",
            Publisher = "federation.test"
        };
        var crossA = new AvailabilityClaim
        {
            Player = "Cross Checked",
            PlayerKey = AvailabilityNewsService.NormalizePlayerKey("Cross Checked"),
            TeamId = "france",
            TeamName = "France",
            Status = AvailabilityClaimStatus.ConfirmedOutInjury,
            EvidenceLevel = AvailabilityEvidenceLevel.ReputableReported,
            SourceUrl = "https://a.test",
            Publisher = "a.test"
        };
        var crossB = new AvailabilityClaim
        {
            Player = "Cross Checked",
            PlayerKey = AvailabilityNewsService.NormalizePlayerKey("Cross Checked"),
            TeamId = "france",
            TeamName = "France",
            Status = AvailabilityClaimStatus.ConfirmedOutInjury,
            EvidenceLevel = AvailabilityEvidenceLevel.ReputableReported,
            SourceUrl = "https://b.test",
            Publisher = "b.test"
        };
        var claims = new[] { singleReputable, official, crossA, crossB };

        AvailabilityNewsService.ApplyPredictionFlags(claims, requireCrossCheck: true);

        Assert.True(singleReputable.AffectsPrediction);
        Assert.True(official.AffectsPrediction);
        Assert.True(crossA.AffectsPrediction);
        Assert.True(crossB.AffectsPrediction);
    }

    [Fact]
    public void AvailabilityNews_ParsesTalkSportTrackerRows()
    {
        var claims = AvailabilityNewsService.ParseTrackerClaims(TalkSportSample(), "https://talksport.test/tracker", "talksport.com").ToList();

        AvailabilityNewsService.ApplyPredictionFlags(claims, requireCrossCheck: true);

        Assert.Equal(19, claims.Count);
        AssertClaim(claims, "Moïse Bombito", "canada", AvailabilityClaimStatus.ConfirmedOutInjury, affects: true);
        AssertClaim(claims, "Wesley França", "brazil", AvailabilityClaimStatus.ConfirmedOutInjury, affects: true);
        AssertClaim(claims, "Nayef Aguerd", "morocco", AvailabilityClaimStatus.Doubtful, affects: false);
        AssertClaim(claims, "Julio Enciso", "paraguay", AvailabilityClaimStatus.Doubtful, affects: false);
        AssertClaim(claims, "Denil Castillo", "ecuador", AvailabilityClaimStatus.Doubtful, affects: false);
        AssertClaim(claims, "Sebastian Caceres", "uruguay", AvailabilityClaimStatus.Doubtful, affects: false);
        AssertClaim(claims, "Chris Richards", "united-states", AvailabilityClaimStatus.Doubtful, affects: false);
        AssertClaim(claims, "Edson Alvarez", "mexico", AvailabilityClaimStatus.Available, affects: false);
        AssertClaim(claims, "Alfie Jones", "canada", AvailabilityClaimStatus.Available, affects: false);
        AssertClaim(claims, "Wataru Endo", "japan", AvailabilityClaimStatus.Available, affects: false);
        AssertClaim(claims, "Abde Ezzalzouli", "morocco", AvailabilityClaimStatus.Doubtful, affects: false);
        AssertClaim(claims, "Noussair Mazraoui", "morocco", AvailabilityClaimStatus.Doubtful, affects: false);
        AssertClaim(claims, "Lamine Yamal", "spain", AvailabilityClaimStatus.Available, affects: false);
        AssertClaim(claims, "Nico Williams", "spain", AvailabilityClaimStatus.Available, affects: false);
        AssertClaim(claims, "Victor Munoz", "spain", AvailabilityClaimStatus.Available, affects: false);
    }

    [Fact]
    public async Task AvailabilityNews_SourceFetchFailureRecordsWarningAndContinues()
    {
        await using var db = await NewDb();
        var service = new AvailabilityNewsService(
            new HttpClient(new FakeHttpMessageHandler(new Dictionary<string, string>())) { BaseAddress = new Uri("https://openrouter.test/") },
            db,
            AvailabilityOptions(["https://missing.test/article"]));

        var report = await service.RefreshAsync();

        Assert.Equal(1, report.SourcesSkipped);
        Assert.NotEmpty(report.Errors);
        Assert.Empty(await db.AvailabilityClaims.ToListAsync());
    }

    [Fact]
    public async Task AvailabilityNews_BotGateIsSkippedWithWarning()
    {
        await using var db = await NewDb();
        var service = new AvailabilityNewsService(
            new HttpClient(new FakeHttpMessageHandler(new Dictionary<string, string>
            {
                ["https://espn.test/article"] = "<html><title>Blocked</title><body>Please enable JavaScript to continue.</body></html>"
            }))
            { BaseAddress = new Uri("https://openrouter.test/") },
            db,
            AvailabilityOptions(["https://espn.test/article"]));

        var report = await service.RefreshAsync();

        Assert.Equal(1, report.SourcesSkipped);
        Assert.Contains(report.Errors, e => e.Contains("bloqueada", StringComparison.OrdinalIgnoreCase) || e.Contains("JavaScript", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AvailabilityNews_DeterministicTrackerRowsAreSavedWhenModelMisses()
    {
        await using var db = await NewDb();
        var sourceUrl = "https://talksport.test/tracker";
        var service = new AvailabilityNewsService(
            new HttpClient(new FakeHttpMessageHandler(new Dictionary<string, string>
            {
                [sourceUrl] = LongArticleHtml(TalkSportSample()),
                ["https://openrouter.test/chat/completions"] = EmptyOpenRouterResponse()
            }))
            { BaseAddress = new Uri("https://openrouter.test/") },
            db,
            AvailabilityOptions([sourceUrl]));

        var report = await service.RefreshAsync();
        var claims = await db.AvailabilityClaims.ToListAsync();

        Assert.Equal(19, report.ClaimsSaved);
        Assert.Equal(2, report.ConfirmedOutClaims);
        Assert.Equal(8, report.DoubtfulClaims);
        Assert.Equal(9, report.AvailableClaims);
        Assert.Contains(claims, c => c.Player == "Moïse Bombito" && c.AffectsPrediction);
        Assert.Contains(claims, c => c.Player == "Edson Alvarez" && c.Status == AvailabilityClaimStatus.Available && !c.AffectsPrediction);
    }

    [Fact]
    public async Task AvailabilityNews_MalformedModelJsonStillSavesDeterministicTrackerRows()
    {
        await using var db = await NewDb();
        var sourceUrl = "https://talksport.test/tracker";
        var service = new AvailabilityNewsService(
            new HttpClient(new FakeHttpMessageHandler(new Dictionary<string, string>
            {
                [sourceUrl] = LongArticleHtml(TalkSportSample()),
                ["https://openrouter.test/chat/completions"] = "not json"
            }))
            { BaseAddress = new Uri("https://openrouter.test/") },
            db,
            AvailabilityOptions([sourceUrl]));

        var report = await service.RefreshAsync();

        Assert.NotEmpty(report.Errors);
        Assert.Equal(19, await db.AvailabilityClaims.CountAsync());
        Assert.Contains(await db.AvailabilityClaims.ToListAsync(), c => c.Player == "Wesley França" && c.AffectsPrediction);
    }

    [Fact]
    public async Task AvailabilityNews_SourceReplacementRemovesStaleClaimsAndKeepsFreshLedger()
    {
        await using var db = await NewDb();
        var sourceUrl = "https://talksport.test/tracker";
        db.AvailabilityClaims.Add(new AvailabilityClaim
        {
            Player = "Stale Player",
            PlayerKey = AvailabilityNewsService.NormalizePlayerKey("Stale Player"),
            TeamId = "canada",
            TeamName = "Canada",
            Status = AvailabilityClaimStatus.ConfirmedOutInjury,
            EvidenceLevel = AvailabilityEvidenceLevel.ReputableReported,
            SourceUrl = sourceUrl,
            Publisher = "talksport.com",
            AffectsPrediction = true
        });
        await db.SaveChangesAsync();
        var service = new AvailabilityNewsService(
            new HttpClient(new FakeHttpMessageHandler(new Dictionary<string, string>
            {
                [sourceUrl] = LongArticleHtml("Moïse Bombito, Canada - Leg soreness following return from broken leg in October 2025 to rule him out of World Cup. OUT."),
                ["https://openrouter.test/chat/completions"] = EmptyOpenRouterResponse()
            }))
            { BaseAddress = new Uri("https://openrouter.test/") },
            db,
            AvailabilityOptions([sourceUrl]));

        await service.RefreshAsync();
        var claims = await db.AvailabilityClaims.ToListAsync();

        Assert.DoesNotContain(claims, c => c.Player == "Stale Player");
        Assert.Contains(claims, c => c.Player == "Moïse Bombito");
    }

    [Fact]
    public async Task AvailabilityNews_OpenRouterFailureKeepsExistingClaims()
    {
        await using var db = await NewDb();
        db.AvailabilityClaims.Add(new AvailabilityClaim
        {
            Player = "Existing",
            PlayerKey = "existing",
            TeamId = "france",
            TeamName = "France",
            Status = AvailabilityClaimStatus.ConfirmedOutInjury,
            EvidenceLevel = AvailabilityEvidenceLevel.Official,
            SourceUrl = "https://source.test/article"
        });
        await db.SaveChangesAsync();

        var service = new AvailabilityNewsService(
            new HttpClient(new FakeHttpMessageHandler(new Dictionary<string, string>
            {
                ["https://source.test/article"] = LongArticleHtml("France confirmed Existing will miss the World Cup.")
            }))
            { BaseAddress = new Uri("https://openrouter.test/") },
            db,
            AvailabilityOptions(["https://source.test/article"]));

        var report = await service.RefreshAsync();

        Assert.NotEmpty(report.Errors);
        Assert.Equal("Existing", Assert.Single(await db.AvailabilityClaims.ToListAsync()).Player);
    }

    [Fact]
    public async Task AvailabilityNews_SingleCuratedOutClaimUpdatesFixtureContextAndSources()
    {
        await using var db = await NewDb();
        var sourceUrl = "https://talksport.test/tracker";
        db.Teams.AddRange(new Team { Id = "mexico", Name = "Mexico" }, new Team { Id = "canada", Name = "Canada" });
        db.Fixtures.Add(new Fixture { Id = "f1", Group = "A", HomeTeamId = "mexico", AwayTeamId = "canada" });
        db.Results.AddRange(
            Result("mexico", "canada", 2, 0),
            Result("mexico", "canada", 1, 0),
            Result("canada", "mexico", 1, 2));
        await db.SaveChangesAsync();
        var service = new AvailabilityNewsService(
            new HttpClient(new FakeHttpMessageHandler(new Dictionary<string, string>
            {
                [sourceUrl] = LongArticleHtml("Moïse Bombito, Canada - Leg soreness following return from broken leg in October 2025 to rule him out of World Cup. OUT."),
                ["https://openrouter.test/chat/completions"] = EmptyOpenRouterResponse()
            }))
            { BaseAddress = new Uri("https://openrouter.test/") },
            db,
            AvailabilityOptions([sourceUrl]));

        await service.RefreshAsync();
        var context = await db.FixtureContexts.FindAsync("f1");
        var prediction = await new PredictionService(db, SimulationOptions(1, 1)).PredictFixtureAsync("f1");

        Assert.NotNull(context);
        Assert.Equal(1, context.UnavailableAwayPlayers);
        Assert.Contains(SourceMetadata.AvailabilityNews, prediction!.Predictions.Single(p => p.PredictorPriority == 5).Sources);
    }

    [Fact]
    public async Task AvailabilityNews_AvailableRowsAreStoredButDoNotAlterUnavailableCounts()
    {
        await using var db = await NewDb();
        var sourceUrl = "https://talksport.test/tracker";
        db.Teams.AddRange(new Team { Id = "mexico", Name = "Mexico" }, new Team { Id = "canada", Name = "Canada" });
        db.Fixtures.Add(new Fixture { Id = "f1", Group = "A", HomeTeamId = "mexico", AwayTeamId = "canada" });
        await db.SaveChangesAsync();
        var service = new AvailabilityNewsService(
            new HttpClient(new FakeHttpMessageHandler(new Dictionary<string, string>
            {
                [sourceUrl] = LongArticleHtml("Edson Alvarez, Mexico - West Ham player underwent ankle surgery in February but is now back fit. IN."),
                ["https://openrouter.test/chat/completions"] = EmptyOpenRouterResponse()
            }))
            { BaseAddress = new Uri("https://openrouter.test/") },
            db,
            AvailabilityOptions([sourceUrl]));

        await service.RefreshAsync();
        var claim = Assert.Single(await db.AvailabilityClaims.ToListAsync());
        var context = await db.FixtureContexts.FindAsync("f1");

        Assert.Equal(AvailabilityClaimStatus.Available, claim.Status);
        Assert.False(claim.AffectsPrediction);
        Assert.NotNull(context);
        Assert.Equal(0, context.UnavailableHomePlayers);
        Assert.Equal(0, context.UnavailableAwayPlayers);
    }

    [Fact]
    public async Task AvailabilityNews_RefreshUpdatesFixtureContextAndPredictionSources()
    {
        await using var db = await NewDb();
        db.Teams.AddRange(new Team { Id = "france", Name = "France" }, new Team { Id = "argentina", Name = "Argentina" });
        db.Fixtures.Add(new Fixture { Id = "f1", Group = "A", HomeTeamId = "france", AwayTeamId = "argentina" });
        db.Results.AddRange(
            Result("france", "argentina", 2, 0),
            Result("france", "argentina", 1, 0),
            Result("argentina", "france", 1, 2));
        db.AvailabilityClaims.Add(new AvailabilityClaim
        {
            Player = "Official Player",
            PlayerKey = AvailabilityNewsService.NormalizePlayerKey("Official Player"),
            TeamId = "france",
            TeamName = "France",
            Status = AvailabilityClaimStatus.ConfirmedOutInjury,
            EvidenceLevel = AvailabilityEvidenceLevel.Official,
            SourceUrl = "https://federation.test",
            Publisher = "federation.test",
            AffectsPrediction = true
        });
        await db.SaveChangesAsync();
        var service = new AvailabilityNewsService(new HttpClient(new FakeHttpMessageHandler(new Dictionary<string, string>())), db, AvailabilityOptions([]));

        await service.RefreshFixtureContextAsync("f1");
        var context = await db.FixtureContexts.FindAsync("f1");
        var prediction = await new PredictionService(db, SimulationOptions(1, 1)).PredictFixtureAsync("f1");

        Assert.NotNull(context);
        Assert.Equal(1, context.UnavailableHomePlayers);
        Assert.True(context.HasAvailabilityNews);
        Assert.Contains(SourceMetadata.AvailabilityNews, prediction!.Predictions.Single(p => p.PredictorPriority == 5).Sources);
    }

    [Fact]
    public void ApiFootball_SquadResponseParsesPlayerPositions()
    {
        var parsed = JsonSerializer.Deserialize<ApiSquadResponse>("""
            {
              "response": [{
                "team": { "id": 2, "name": "France" },
                "players": [
                  { "id": 278, "name": "Kylian Mbappé", "position": "Attacker" },
                  { "id": 22090, "name": "W. Saliba", "position": "Defender" }
                ]
              }]
            }
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(parsed);
        Assert.Equal("Attacker", parsed.Response[0].Players[0].Position);
        Assert.Equal("Defender", parsed.Response[0].Players[1].Position);
    }

    [Fact]
    public void ApiFootball_PlayerRoleMatchingHandlesAccentsAndInitialLastNames()
    {
        var candidates = new[]
        {
            new PlayerRoleCandidate(278, "Kylian Mbappé", "Attacker", "test"),
            new PlayerRoleCandidate(22090, "William Saliba", "Defender", "test")
        };

        var accent = ApiFootballService.MatchPlayerRole("Kylian Mbappe", candidates);
        var initial = ApiFootballService.MatchPlayerRole("W. Saliba", candidates);

        Assert.Equal(278, accent?.Id);
        Assert.Equal("Attacker", accent?.Position);
        Assert.Equal(22090, initial?.Id);
        Assert.Equal("Defender", initial?.Position);
    }

    [Fact]
    public void AvailabilityNews_PositionImpactsUseUnknownFallbackAndClampTotals()
    {
        Assert.Equal((0.020, 0.000), AvailabilityNewsService.ImpactForPosition("Unknown"));

        var clamped = AvailabilityNewsService.SumImpacts(Enumerable.Repeat("Goalkeeper", 10));

        Assert.Equal(0.0, clamped.Attack);
        Assert.Equal(0.18, clamped.Defense);
    }

    [Fact]
    public void ContextModel_AttackerAbsenceReducesOwnXgMoreThanDefenderAbsence()
    {
        var goal = new GoalModel(
        [
            Result("a", "b", 2, 0),
            Result("a", "b", 1, 0),
            Result("b", "a", 1, 2)
        ]);
        var attackerContext = TestContext(fixtureContext: new FixtureContext
        {
            FixtureId = "test",
            UnavailableHomePlayers = 1,
            UnavailableHomeAttackImpact = AvailabilityNewsService.ImpactForPosition("Attacker").Attack,
            UnavailableHomeDefenseImpact = AvailabilityNewsService.ImpactForPosition("Attacker").Defense
        });
        var defenderContext = TestContext(fixtureContext: new FixtureContext
        {
            FixtureId = "test",
            UnavailableHomePlayers = 1,
            UnavailableHomeAttackImpact = AvailabilityNewsService.ImpactForPosition("Defender").Attack,
            UnavailableHomeDefenseImpact = AvailabilityNewsService.ImpactForPosition("Defender").Defense
        });

        var attackerPrediction = new GoalPlusRecentContextModel(goal).Predict(attackerContext);
        var defenderPrediction = new GoalPlusRecentContextModel(goal).Predict(defenderContext);

        Assert.True(attackerPrediction.ExpectedHomeGoals < defenderPrediction.ExpectedHomeGoals);
    }

    [Fact]
    public void ContextModel_DefenderAbsenceRaisesOpponentXg()
    {
        var goal = new GoalModel(
        [
            Result("a", "b", 2, 0),
            Result("a", "b", 1, 0),
            Result("b", "a", 1, 2)
        ]);
        var baseline = new GoalPlusRecentContextModel(goal).Predict(TestContext());
        var defenderContext = TestContext(fixtureContext: new FixtureContext
        {
            FixtureId = "test",
            UnavailableHomePlayers = 1,
            UnavailableHomeAttackImpact = AvailabilityNewsService.ImpactForPosition("Defender").Attack,
            UnavailableHomeDefenseImpact = AvailabilityNewsService.ImpactForPosition("Defender").Defense
        });

        var prediction = new GoalPlusRecentContextModel(goal).Predict(defenderContext);

        Assert.True(prediction.ExpectedAwayGoals > baseline.ExpectedAwayGoals);
    }

    [Fact]
    public async Task ApiFootball_RoleEnrichmentUpdatesClaimsWithoutDeletingEvidence()
    {
        await using var db = await NewDb();
        db.AvailabilityClaims.Add(new AvailabilityClaim
        {
            Player = "Kylian Mbappe",
            PlayerKey = AvailabilityNewsService.NormalizePlayerKey("Kylian Mbappe"),
            TeamId = "france",
            TeamName = "France",
            Status = AvailabilityClaimStatus.ConfirmedOutInjury,
            EvidenceLevel = AvailabilityEvidenceLevel.Official,
            SourceUrl = "https://source.test",
            SupportingQuote = "France confirmed Kylian Mbappe will miss the match.",
            AffectsPrediction = true
        });
        await db.SaveChangesAsync();
        var handler = new FakeHttpMessageHandler(new Dictionary<string, string>
        {
            ["https://api.test/teams?league=1&season=2026"] = """
                {"response":[{"team":{"id":2,"name":"France"}}]}
                """,
            ["https://api.test/players/squads?team=2"] = """
                {"response":[{"team":{"id":2,"name":"France"},"players":[{"id":278,"name":"Kylian Mbappé","position":"Attacker"}]}]}
                """
        });
        var api = ApiService(db, handler);

        var report = await api.EnrichAvailabilityRolesAsync();
        var claim = Assert.Single(await db.AvailabilityClaims.ToListAsync());

        Assert.Equal(1, report.RoleMatchedClaims);
        Assert.Equal(278, claim.ApiFootballPlayerId);
        Assert.Equal("Attacker", claim.Position);
        Assert.Equal("France confirmed Kylian Mbappe will miss the match.", claim.SupportingQuote);
    }

    [Fact]
    public async Task ApiFootball_SquadFailureLeavesClaimsUnknown()
    {
        await using var db = await NewDb();
        db.AvailabilityClaims.Add(new AvailabilityClaim
        {
            Player = "Mystery Player",
            PlayerKey = AvailabilityNewsService.NormalizePlayerKey("Mystery Player"),
            TeamId = "france",
            TeamName = "France",
            Status = AvailabilityClaimStatus.ConfirmedOutInjury,
            EvidenceLevel = AvailabilityEvidenceLevel.Official,
            SourceUrl = "https://source.test",
            AffectsPrediction = true
        });
        await db.SaveChangesAsync();
        var handler = new FakeHttpMessageHandler(new Dictionary<string, string>
        {
            ["https://api.test/teams?league=1&season=2026"] = """
                {"response":[{"team":{"id":2,"name":"France"}}]}
                """
        });
        var api = ApiService(db, handler);

        var report = await api.EnrichAvailabilityRolesAsync();
        var claim = Assert.Single(await db.AvailabilityClaims.ToListAsync());

        Assert.Equal(1, report.RoleUnknownClaims);
        Assert.Equal("Unknown", claim.Position);
    }

    [Fact]
    public async Task AvailabilityNews_RoleAwareFixtureContextStoresImpacts()
    {
        await using var db = await NewDb();
        db.Teams.AddRange(new Team { Id = "france", Name = "France" }, new Team { Id = "argentina", Name = "Argentina" });
        db.Fixtures.Add(new Fixture { Id = "f1", Group = "A", HomeTeamId = "france", AwayTeamId = "argentina" });
        db.AvailabilityClaims.Add(new AvailabilityClaim
        {
            Player = "Kylian Mbappe",
            PlayerKey = AvailabilityNewsService.NormalizePlayerKey("Kylian Mbappe"),
            TeamId = "france",
            TeamName = "France",
            Status = AvailabilityClaimStatus.ConfirmedOutInjury,
            EvidenceLevel = AvailabilityEvidenceLevel.Official,
            SourceUrl = "https://source.test",
            AffectsPrediction = true,
            Position = "Attacker"
        });
        await db.SaveChangesAsync();
        var service = new AvailabilityNewsService(new HttpClient(new FakeHttpMessageHandler(new Dictionary<string, string>())), db, AvailabilityOptions([]));

        await service.RefreshFixtureContextAsync("f1");
        var context = await db.FixtureContexts.FindAsync("f1");

        Assert.NotNull(context);
        Assert.Equal(0.035, context.UnavailableHomeAttackImpact, 3);
        Assert.Equal(0.003, context.UnavailableHomeDefenseImpact, 3);
    }

    [Fact]
    public async Task CsvImport_CreatesTeamsGroupsFixturesRatingsAndResults()
    {
        await using var db = await NewDb();
        var importer = new CsvImportService(db, new TestEnvironment(WebProjectRoot()));

        var report = await importer.ImportAllAsync();

        Assert.True(report.Teams >= 48);
        Assert.Equal(12, report.Groups);
        Assert.Equal(72, report.Fixtures);
        Assert.True(report.Ratings > 0);
        Assert.True(report.Results > 0);
        Assert.Equal(ExpectedUniqueHistoricalResultIds(), report.Results);
        Assert.DoesNotContain(await db.Fixtures.ToListAsync(), f => string.IsNullOrWhiteSpace(f.Group));
    }

    [Fact]
    public async Task PredictionService_BulkPredictsImportedGroupFixtures()
    {
        await using var db = await ImportedDb();
        var fixtures = await db.Fixtures.AsNoTracking().ToListAsync();
        var service = new PredictionService(db, SimulationOptions(1, 1));

        var results = await service.PredictFixturesAsync(fixtures);

        Assert.Equal(72, results.Count);
        Assert.All(results, result =>
        {
            Assert.False(string.IsNullOrWhiteSpace(result.Fixture.Id));
            Assert.True(result.BestPrediction.Outcome.IsValid);
        });
    }

    [Fact]
    public async Task PredictionService_BulkPredictionsMatchSingleFixturePredictions()
    {
        await using var db = await ImportedDb();
        var fixtures = await db.Fixtures
            .AsNoTracking()
            .OrderBy(f => f.Group)
            .ThenBy(f => f.HomeTeamId)
            .ThenBy(f => f.AwayTeamId)
            .Take(3)
            .ToListAsync();
        var service = new PredictionService(db, SimulationOptions(1, 1));

        var bulk = await service.PredictFixturesAsync(fixtures);

        foreach (var fixture in fixtures)
        {
            var expected = await service.PredictFixtureAsync(fixture.Id);
            var actual = bulk.Single(result => result.Fixture.Id == fixture.Id);

            Assert.NotNull(expected);
            AssertPredictionResultEqual(expected, actual);
        }
    }

    [Fact]
    public async Task PredictionService_BulkPredictionUsesFixtureIdsWhenTeamsAreMissing()
    {
        await using var db = await NewDb();
        var fixture = new Fixture { Id = "f1", Group = "A", HomeTeamId = "ghost-home", AwayTeamId = "ghost-away" };
        db.Fixtures.Add(fixture);
        await db.SaveChangesAsync();
        var service = new PredictionService(db, SimulationOptions(1, 1));

        var result = Assert.Single(await service.PredictFixturesAsync([fixture]));

        Assert.Equal("ghost-home", result.HomeTeamName);
        Assert.Equal("ghost-away", result.AwayTeamName);
        Assert.True(result.BestPrediction.Outcome.IsValid);
    }

    [Fact]
    public async Task Evaluation_StoresFixtureLevelKnownResult()
    {
        await using var db = await NewDb();
        var fixture = new Fixture { Id = "f1", Group = "A", HomeTeamId = "a", AwayTeamId = "b" };
        db.Teams.AddRange(new Team { Id = "a", Name = "A" }, new Team { Id = "b", Name = "B" });
        db.Fixtures.Add(fixture);
        db.Snapshots.Add(new PredictionSnapshot
        {
            Kind = "match",
            FixtureId = "f1",
            ModelName = "Oráculo final",
            InputSummaryHash = "hash",
            PayloadJson = "{}",
            Explanation = "test",
            HomeWin = .6,
            Draw = .2,
            AwayWin = .2
        });
        await db.SaveChangesAsync();

        var count = await new EvaluationService(db).EvaluateLatestSnapshotAsync(fixture, 2, 1);

        Assert.Equal(1, count);
        Assert.True(fixture.IsPlayed);
        Assert.Equal(2, fixture.HomeGoals);
        Assert.Equal(1, fixture.AwayGoals);
    }

    [Fact]
    public async Task SnapshotService_SavesTournamentSnapshotAgainstLegacyNonNullProbabilityColumns()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE "Snapshots" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_Snapshots" PRIMARY KEY AUTOINCREMENT,
                    "Kind" TEXT NOT NULL,
                    "FixtureId" TEXT NULL,
                    "ModelName" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "InputSummaryHash" TEXT NOT NULL,
                    "PayloadJson" TEXT NOT NULL,
                    "Explanation" TEXT NOT NULL,
                    "HomeWin" REAL NOT NULL,
                    "Draw" REAL NOT NULL,
                    "AwayWin" REAL NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<OloraculoDbContext>().UseSqlite(connection).Options;
        await using var db = new OloraculoDbContext(options);

        var snapshot = await new SnapshotService(db).SaveTournamentAsync(new TournamentProjection
        {
            ModelName = "Final",
            InputSummaryHash = "hash",
            Simulations = 1,
            Teams = []
        });

        Assert.Equal("tournament", snapshot.Kind);
        Assert.Equal(0, snapshot.AwayWin);
    }

    [Fact]
    public async Task SnapshotService_SavesMatchSnapshotsInBulk()
    {
        await using var db = await NewDb();
        var first = Prediction(4, "Final", .6, .2, .2);
        var second = Prediction(4, "Final", .2, .3, .5);
        first.FixtureId = "f1";
        second.FixtureId = "f2";
        var service = new SnapshotService(db);

        var snapshots = await service.SaveMatchesAsync([first, second]);

        Assert.Equal(2, snapshots.Count);
        Assert.Equal(["f1", "f2"], snapshots.Select(snapshot => snapshot.FixtureId));
        Assert.Equal(2, await db.Snapshots.CountAsync(snapshot => snapshot.Kind == "match"));
    }

    [Fact]
    public async Task SnapshotService_ListsTournamentSnapshotsNewestFirstAndExcludesMatches()
    {
        await using var db = await NewDb();
        var service = new SnapshotService(db);
        var oldSnapshot = await service.SaveTournamentAsync(TournamentProjection("old-hash", 100, DateTimeOffset.Parse("2026-01-01T00:00:00Z")));
        db.Snapshots.Add(new PredictionSnapshot
        {
            Kind = "match",
            FixtureId = "f1",
            ModelName = "Match",
            CreatedAt = DateTimeOffset.Parse("2026-01-03T00:00:00Z"),
            InputSummaryHash = "match-hash",
            PayloadJson = "{}",
            Explanation = "match",
            HomeWin = .4,
            Draw = .3,
            AwayWin = .3
        });
        await db.SaveChangesAsync();
        var newSnapshot = await service.SaveTournamentAsync(TournamentProjection("new-hash", 200, DateTimeOffset.Parse("2026-01-02T00:00:00Z")));

        var snapshots = await service.TournamentSnapshotsAsync();

        Assert.Equal([newSnapshot.Id, oldSnapshot.Id], snapshots.Select(s => s.Id));
        Assert.Equal(200, snapshots[0].Simulations);
        Assert.All(snapshots, s => Assert.True(s.IsValid));
    }

    [Fact]
    public async Task SnapshotService_LoadsTournamentSnapshotPayload()
    {
        await using var db = await NewDb();
        var service = new SnapshotService(db);
        var snapshot = await service.SaveTournamentAsync(TournamentProjection("hash", 123, DateTimeOffset.Parse("2026-01-01T00:00:00Z")));

        var result = await service.LoadTournamentSnapshotAsync(snapshot.Id);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Projection);
        Assert.Equal(123, result.Projection.Simulations);
        Assert.Equal("argentina", result.Projection.Teams.Single().TeamId);
        Assert.Equal(.42, result.Projection.Teams.Single().WinTournament);
    }

    [Fact]
    public async Task SnapshotService_SurfacesMalformedTournamentSnapshotPayloads()
    {
        await using var db = await NewDb();
        db.Snapshots.Add(new PredictionSnapshot
        {
            Kind = "tournament",
            ModelName = "Final",
            CreatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            InputSummaryHash = "bad-hash",
            PayloadJson = "not json",
            Explanation = "bad",
            HomeWin = 0,
            Draw = 0,
            AwayWin = 0
        });
        await db.SaveChangesAsync();
        var service = new SnapshotService(db);

        var snapshots = await service.TournamentSnapshotsAsync();
        var result = await service.LoadTournamentSnapshotAsync(snapshots.Single().Id);

        Assert.False(snapshots.Single().IsValid);
        Assert.Null(snapshots.Single().Simulations);
        Assert.False(result.IsValid);
        Assert.Null(result.Projection);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task Simulation_IsRepeatableWithSameSeed()
    {
        await using var db = await ImportedDb();
        var service = Simulation(db, simulations: 3, seed: 42);

        var one = await service.RunAsync(saveSnapshot: false);
        var two = await service.RunAsync(saveSnapshot: false);

        Assert.Equal(one.Teams.Select(t => t.WinTournament), two.Teams.Select(t => t.WinTournament));
        Assert.Equal(1.0, one.Teams.Sum(t => t.WinTournament), 6);
    }

    [Theory]
    [InlineData("argentina", "france")]
    [InlineData("france", "argentina")]
    [InlineData("mexico", "canada")]
    public async Task SimulationPredictionContext_MatchesPredictionServiceForPairs(string homeId, string awayId)
    {
        await using var db = await ImportedDb();
        var options = SimulationOptions(simulations: 1, seed: 42);
        var prediction = new PredictionService(db, options);
        var simulationPrediction = await SimulationPredictionContext.CreateAsync(db, options.Value);

        var expected = await prediction.PredictPairAsync(homeId, awayId);
        var actual = await simulationPrediction.PredictPairAsync(homeId, awayId);

        AssertPredictionResultEqual(expected, actual);
    }

    [Fact]
    public async Task Simulation_WithFixedSeedKeepsDeterministicTournamentOutput()
    {
        await using var db = await ImportedDb();
        var service = Simulation(db, simulations: 2, seed: 2026);

        var one = await service.RunAsync(saveSnapshot: false);
        var two = await service.RunAsync(saveSnapshot: false);

        Assert.Equal(2, one.Simulations);
        Assert.Equal(1.0, one.Teams.Sum(t => t.WinTournament), 6);
        Assert.Equal(one.Teams.Select(ProjectionKey), two.Teams.Select(ProjectionKey));
    }

    [Fact]
    public async Task Simulation_UsesKnownGroupFixtureScores()
    {
        await using var db = await ImportedDb();
        var mexicoFixtures = await db.Fixtures
            .Where(f => f.Group == "A" && (f.HomeTeamId == "mexico" || f.AwayTeamId == "mexico"))
            .ToListAsync();

        foreach (var fixture in mexicoFixtures)
        {
            fixture.IsPlayed = true;
            fixture.HomeGoals = fixture.HomeTeamId == "mexico" ? 10 : 0;
            fixture.AwayGoals = fixture.AwayTeamId == "mexico" ? 10 : 0;
        }
        await db.SaveChangesAsync();

        var projection = await Simulation(db, simulations: 5, seed: 7).RunAsync(saveSnapshot: false);
        var mexico = projection.Teams.Single(t => t.TeamId == "mexico");

        Assert.Equal(1.0, mexico.WinGroup, 6);
        Assert.Equal(1.0, mexico.Qualify, 6);
    }

    private static SimulationService Simulation(OloraculoDbContext db, int simulations, int seed)
    {
        var options = SimulationOptions(simulations, seed);
        var prediction = new PredictionService(db, options);
        var snapshots = new SnapshotService(db);
        return new SimulationService(db, prediction, snapshots, options);
    }

    private static IOptions<OloraculoConfig> SimulationOptions(int simulations, int seed) =>
        Options.Create(new OloraculoConfig
        {
            GoalModelYearsWindow = 3,
            RecentResultCount = 8,
            SimulationCount = simulations,
            SimulationSeed = seed
        });

    private static IOptions<OloraculoConfig> AvailabilityOptions(string[] sources) =>
        Options.Create(new OloraculoConfig
        {
            OpenRouterApiKey = "test-key",
            OpenRouterBaseUrl = "https://openrouter.test/",
            OpenRouterModel = "test-model",
            AvailabilitySourceUrls = sources,
            AvailabilityMaxArticleChars = 4000,
            AvailabilityRequireCrossCheck = true
        });

    private static ApiFootballService ApiService(OloraculoDbContext db, HttpMessageHandler handler)
    {
        var options = Options.Create(new OloraculoConfig
        {
            ApiFootballApiKey = "test-key",
            ApiFootballBaseUrl = "https://api.test/",
            ApiFootballLeagueId = 1,
            ApiFootballSeason = 2026,
            OpenRouterApiKey = "test-key",
            OpenRouterBaseUrl = "https://openrouter.test/",
            AvailabilitySourceUrls = []
        });
        var availability = new AvailabilityNewsService(
            new HttpClient(new FakeHttpMessageHandler(new Dictionary<string, string>())) { BaseAddress = new Uri("https://openrouter.test/") },
            db,
            options);

        return new ApiFootballService(new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") }, db, options, availability);
    }

    private static async Task<OloraculoDbContext> ImportedDb()
    {
        var db = await NewDb();
        await new CsvImportService(db, new TestEnvironment(WebProjectRoot())).ImportAllAsync();
        return db;
    }

    private static async Task<OloraculoDbContext> NewDb()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<OloraculoDbContext>().UseSqlite(connection).Options;
        var db = new OloraculoDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static MatchContext TestContext(string homeId = "a", string awayId = "b", FixtureContext? fixtureContext = null) => new()
    {
        Fixture = new Fixture { Id = "test", HomeTeamId = homeId, AwayTeamId = awayId, NeutralVenue = true },
        HomeTeam = new Team { Id = homeId, Name = homeId.ToUpperInvariant() },
        AwayTeam = new Team { Id = awayId, Name = awayId.ToUpperInvariant() },
        HomeElo = new Rating { TeamId = homeId, Type = RatingTypeEnum.Elo, Value = 1800, Source = "test" },
        AwayElo = new Rating { TeamId = awayId, Type = RatingTypeEnum.Elo, Value = 1700, Source = "test" },
        HomeRecentMatchHistory = [],
        AwayRecentMatchHistory = [],
        FixtureContext = fixtureContext
    };

    private static MatchResult Result(string home, string away, int homeGoals, int awayGoals) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        HomeTeamId = home,
        AwayTeamId = away,
        HomeGoals = homeGoals,
        AwayGoals = awayGoals,
        Date = DateTimeOffset.UtcNow.AddDays(-Random.Shared.Next(1, 30)),
        Tournament = "test",
        Neutral = true,
        Source = "test"
    };

    private static TournamentProjection TournamentProjection(string hash, int simulations, DateTimeOffset generatedAt) => new()
    {
        GeneratedAt = generatedAt,
        Simulations = simulations,
        ModelName = "Final",
        InputSummaryHash = hash,
        Teams =
        [
            new TeamTournamentProbability
            {
                TeamId = "argentina",
                Group = "A",
                Qualify = .8,
                ReachRoundOf16 = .7,
                ReachQuarterFinal = .6,
                ReachSemiFinal = .5,
                ReachFinal = .45,
                WinTournament = .42
            }
        ]
    };

    private static MatchPrediction Prediction(
        int priority,
        string name,
        double home,
        double draw,
        double away,
        bool degraded = false,
        IReadOnlyList<string>? missing = null,
        ScorelineDistribution? scoreline = null,
        IReadOnlyList<SourceMetadata>? sources = null) => new()
    {
        PredictorPriority = priority,
        PredictorName = name,
        FixtureId = "f",
        HomeTeamId = "a",
        AwayTeamId = "b",
        Outcome = new OutcomeProbabilities(home, draw, away).Normalize(),
        Scoreline = scoreline,
        Explanation = name,
        FeaturesMissing = missing ?? [],
        Sources = sources ?? [],
        Degraded = degraded
    };

    private static void AssertPredictionResultEqual(MatchPredictionResult expected, MatchPredictionResult actual)
    {
        Assert.Equal(expected.Fixture.Id, actual.Fixture.Id);
        Assert.Equal(expected.Fixture.HomeTeamId, actual.Fixture.HomeTeamId);
        Assert.Equal(expected.Fixture.AwayTeamId, actual.Fixture.AwayTeamId);
        Assert.Equal(expected.HomeTeamName, actual.HomeTeamName);
        Assert.Equal(expected.AwayTeamName, actual.AwayTeamName);
        Assert.Equal(expected.Predictions.Count, actual.Predictions.Count);

        for (var i = 0; i < expected.Predictions.Count; i++)
            AssertPredictionEqual(expected.Predictions[i], actual.Predictions[i]);

        AssertPredictionEqual(expected.BestPrediction, actual.BestPrediction);
    }

    private static void AssertPredictionEqual(MatchPrediction expected, MatchPrediction actual)
    {
        Assert.Equal(expected.PredictorName, actual.PredictorName);
        Assert.Equal(expected.PredictorPriority, actual.PredictorPriority);
        Assert.Equal(expected.FixtureId, actual.FixtureId);
        Assert.Equal(expected.HomeTeamId, actual.HomeTeamId);
        Assert.Equal(expected.AwayTeamId, actual.AwayTeamId);
        Assert.Equal(expected.Outcome.HomeWin, actual.Outcome.HomeWin);
        Assert.Equal(expected.Outcome.Draw, actual.Outcome.Draw);
        Assert.Equal(expected.Outcome.AwayWin, actual.Outcome.AwayWin);
        Assert.Equal(expected.ExpectedHomeGoals, actual.ExpectedHomeGoals);
        Assert.Equal(expected.ExpectedAwayGoals, actual.ExpectedAwayGoals);
        Assert.Equal(expected.MostLikelyScore, actual.MostLikelyScore);
        Assert.Equal(expected.Degraded, actual.Degraded);
        Assert.Equal(expected.FeaturesMissing, actual.FeaturesMissing);
        AssertScorelineEqual(expected.Scoreline, actual.Scoreline);
    }

    private static void AssertScorelineEqual(ScorelineDistribution? expected, ScorelineDistribution? actual)
    {
        Assert.Equal(expected is null, actual is null);
        if (expected is null || actual is null)
            return;

        Assert.Equal(expected.MaxGoals, actual.MaxGoals);
        for (var home = 0; home <= expected.MaxGoals; home++)
            for (var away = 0; away <= expected.MaxGoals; away++)
                Assert.Equal(expected.Probability(home, away), actual.Probability(home, away));
    }

    private static object ProjectionKey(TeamTournamentProbability team) => new
    {
        team.TeamId,
        team.Group,
        team.WinGroup,
        team.Qualify,
        team.ReachRoundOf16,
        team.ReachQuarterFinal,
        team.ReachSemiFinal,
        team.ReachFinal,
        team.WinTournament,
        team.ExpectedGroupPoints
    };

    private static string WebProjectRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "Oloraculo.Web");
            if (File.Exists(Path.Combine(candidate, "Data", OloraculoDataFiles.GroupsCsv)))
                return candidate;

            current = current.Parent;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Oloraculo.Web"));
    }

    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "OloraculoTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string SampleFifaRaw() =>
        """
        local data = {}
        data.updated  = { day = 1, month = 'April', year =2026 }
        data.rankings = {
            { "France", 1, 2, 1877.32 },
            { "Spain", 2, -1, 1876.40 },
        }
        """;

    private static string SampleEloHtml() =>
        """
        <html><body>
        <h2>World football Elo ratings as on June 5th, 2026</h2>
        <p>1 . Image: Spain Spain 2155 2 . Argentina 2113</p>
        <p>About International-football.net</p>
        </body></html>
        """;

    private static string LongArticleHtml(string body) =>
        $"""
        <html><head><title>Availability tracker</title></head><body>
        <article>
        {body}
        This article contains enough surrounding text to be accepted by the parser for testing purposes. It repeats the availability details with clear sourcing and additional tournament context so the stripped text is long enough for the service to send to the model.
        </article>
        </body></html>
        """;

    private static string EmptyOpenRouterResponse() =>
        """
        {"choices":[{"message":{"content":"{\"claims\":[]}"}}]}
        """;

    private static string TalkSportSample() =>
        """
        World Cup 2026 injury tracker
        Edson Alvarez, Mexico - West Ham player underwent ankle surgery in February but is now back fit. IN.
        Alfie Jones, Canada - English-born defender underwent ankle surgery playing for Middlesbrough but is now back fit. IN.
        Moïse Bombito, Canada - Leg soreness following return from broken leg in October 2025 to rule him out of World Cup. OUT.
        Amir Hadziahmetovic, Bosnia - Hull City loanee underwent surgery for a meniscus injury in April but is now back fit. IN.
        Wesley França, Brazil - Thigh injury suffered in 2-1 win over Egypt and now replaced by Manchester United-bound Ederson. OUT.
        Nayef Aguerd, Morocco - Ex-West Ham defender has not played since March 4 due to groin issues. MAJOR DOUBT.
        Abde Ezzalzouli and Noussair Mazraoui, Morocco - Both players forced off with injuries in Sunday's 1-1 draw with Norway. MAJOR DOUBTS.
        Julio Enciso, Paraguay - Former Brighton player stretchered off in tears during 4-0 win over Nicaragua. MAJOR DOUBT.
        Denil Castillo, Ecuador - Midtjylland midfielder withdrew from the March squad through injury but featured in a 2-1 win over Saudi Arabia last month. He was then left out of the squad for 3-0 victory against Guatemala. DOUBT.
        Jurrien Timber, Netherlands - Arsenal star returned off the bench in the Champions League final from an ankle injury suffered on March 13 but still lacking match fitness. DOUBT.
        Wataru Endo, Japan - Liverpool midfielder sustained an ankle ligament injury on February 11 but returned for Japan's pre-World Cup friendly win over Iceland. IN.
        Tyler Bindon, New Zealand - Ankle injury saw him miss Sheffield United's last two games of the season but featured off the bench in his country's 1-0 defeat to England. IN.
        Lamine Yamal, Nico Williams and Victor Munoz, Spain - See above for further details. IN.
        Sebastian Caceres, Uruguay - Called up by Marcelo Bielsa despite suffering a facial fracture playing for Mexican club America. DOUBT.
        Cristian Romero, Argentina - Tottenham captain missed remainder of club season with a knee injury suffered in a 1-0 loss at Sunderland on April 12 but returned off the bench in his country's 2-0 win over Honduras. IN.
        Chris Richards, USMNT - Unused substitute for Conference League final after injuring ankle ligaments for Crystal Palace against Brentford on May 17. DOUBT.
        """;

    private static void AssertClaim(IReadOnlyList<AvailabilityClaim> claims, string player, string teamId, AvailabilityClaimStatus status, bool affects)
    {
        var claim = Assert.Single(claims, c => c.Player == player);
        Assert.Equal(teamId, claim.TeamId);
        Assert.Equal(status, claim.Status);
        Assert.Equal(affects, claim.AffectsPrediction);
    }

    private static int ExpectedUniqueHistoricalResultIds()
    {
        var rows = CsvParsingHelper.ReadCsv<HistoricalResultCsvRow>(Path.Combine(WebProjectRoot(), "Data", OloraculoDataFiles.HistoricalResultsCsv));
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (!DateTimeOffset.TryParse(row.Date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ||
                !int.TryParse(row.HomeScore, NumberStyles.Integer, CultureInfo.InvariantCulture, out var homeScore) ||
                !int.TryParse(row.AwayScore, NumberStyles.Integer, CultureInfo.InvariantCulture, out var awayScore))
            {
                continue;
            }

            var homeId = TeamNameNormalizer.ToId(row.HomeTeam);
            var awayId = TeamNameNormalizer.ToId(row.AwayTeam);
            ids.Add(CryptoUtil.GetSha256($"{homeId}-{awayId}-{date:O}-{row.Tournament}-{homeScore}-{awayScore}"));
        }

        return ids.Count;
    }

    private sealed class TestEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Oloraculo.Web.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FakeHttpMessageHandler(IReadOnlyDictionary<string, string> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri?.ToString() ?? "";
            if (!responses.TryGetValue(uri, out var content))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content)
            });
        }
    }
}
