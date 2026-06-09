namespace Oloraculo.Web.Models.ApiFootballModels
{
    public class ApiPlayerStatsRow
    {
        public ApiPlayerStatsPlayer Player { get; set; } = new();
        public List<ApiPlayerStatistic> Statistics { get; set; } = [];
    }
}
