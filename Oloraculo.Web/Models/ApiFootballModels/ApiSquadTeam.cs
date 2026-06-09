namespace Oloraculo.Web.Models.ApiFootballModels
{
    public class ApiSquadTeam
    {
        public ApiTeam Team { get; set; } = new();
        public List<ApiSquadPlayer> Players { get; set; } = [];
    }
}
