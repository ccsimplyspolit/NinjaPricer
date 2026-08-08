using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using NinjaPricer.API.PoeNinja;
using NinjaPricer.API.PoeNinja.Models;

var tests = new (string Name, Action Body)[]
{
    ("exchange lines map by id and name", ExchangeLinesMapByIdAndName),
    ("duplicate or malformed exchange rows fail closed", DuplicateOrMalformedRowsFailClosed),
    ("divine to exalted rate handles both primaries", DivineToExaltedRateHandlesBothPrimaries),
    ("same-league merge preserves missing categories only", SameLeagueMergePreservesMissingCategoriesOnly),
};

var failures = 0;
foreach (var (name, body) in tests)
{
    try
    {
        body();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

if (failures != 0)
    throw new InvalidOperationException($"{failures} NinjaPricer fixture test(s) failed.");

Console.WriteLine($"{tests.Length} NinjaPricer fixture tests passed.");

static void ExchangeLinesMapByIdAndName()
{
    var overview = JsonConvert.DeserializeObject<ExchangeOverview>("""
    {
      "core": { "primary": "divine", "rates": { "exalted": 42.5 } },
      "lines": [ { "id": "exalted", "primaryValue": 42.5 } ],
      "items": [ { "id": "exalted", "name": "Exalted Orb" } ]
    }
    """)!;

    Assert(overview.LinesByName.TryGetValue("exalted orb", out var pair), "case-insensitive item lookup");
    Assert(pair.Line.PrimaryValue == 42.5, "line value");
    Assert(pair.Item.Id == "exalted", "item id");
}

static void DuplicateOrMalformedRowsFailClosed()
{
    var overview = new ExchangeOverview
    {
        Lines = [new ExchangeLine { Id = "ok", PrimaryValue = 1.0 }],
        Items =
        [
            new ExchangeItem { Id = "ok", Name = "Orb" },
            new ExchangeItem { Id = "ok", Name = "ORB" },
            new ExchangeItem { Id = "missing", Name = "No line" },
            new ExchangeItem { Id = "", Name = "No id" },
            null!
        ]
    };

    Assert(overview.LinesByName.Count == 1, "only first valid duplicate survives");
    Assert(overview.LinesByName.ContainsKey("orb"), "valid row remains available");
}

static void DivineToExaltedRateHandlesBothPrimaries()
{
    var divine = new CollectiveApiData
    {
        Currency = new ExchangeOverview
        {
            Core = new CoreData { Primary = "divine", Rates = new Rates { Exalted = 12.25 } }
        }
    };
    Assert(divine.DivineToExaltedRateRaw == 12.25, "divine primary rate");

    var exalted = new CollectiveApiData
    {
        Currency = new ExchangeOverview
        {
            Core = new CoreData { Primary = "exalted" },
            Lines = [new ExchangeLine { Id = "divine", PrimaryValue = 9.5 }]
        }
    };
    Assert(exalted.DivineToExaltedRateRaw == 9.5, "exalted primary rate");
    Assert(new CollectiveApiData().DivineToExaltedRateRaw == 0, "missing rate is zero");
}

static void SameLeagueMergePreservesMissingCategoriesOnly()
{
    var previous = new CollectiveApiData
    {
        Currency = new ExchangeOverview(),
        Tablets = new StashOverview(),
        DivineToExaltedRate = 7.0
    };
    var current = new CollectiveApiData
    {
        Currency = new ExchangeOverview(),
        DivineToExaltedRate = double.NaN
    };

    current.MergeMissingFrom(previous);
    Assert(current.Tablets != null, "missing stash category restored");
    Assert(current.DivineToExaltedRate == 7.0, "invalid derived rate restored");
    Assert(current.Currency != null, "existing category retained");
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
