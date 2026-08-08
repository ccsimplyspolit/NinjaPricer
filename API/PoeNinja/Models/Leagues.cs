namespace NinjaPricer.API.PoeNinja.Models;

public class LeagueRoot
{
    public Economyleague[] economyLeagues { get; set; }
}

public class PoeNinjaLeague
{
    public string id { get; set; }
    public string name { get; set; }
}

public class Economyleague
{
    public string name { get; set; }
    public string url { get; set; }
    public string displayName { get; set; }
    public bool hardcore { get; set; }
    public bool indexed { get; set; }
}
