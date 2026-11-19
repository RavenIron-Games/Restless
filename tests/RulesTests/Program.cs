using System;
using RavenIron.Restless;

int failed = 0, passed = 0;
void Check(string name, bool ok) { if (ok) passed++; else { failed++; Console.WriteLine("FAIL " + name); } }

const double Day = 1200;
// Unstamped never due: the count starts at first sight, not at world creation.
Check("unstamped is never due", !Rules.IsDue(Rules.Unstamped, 1e9, 30, Day));
Check("just under the interval is not due", !Rules.IsDue(1000, 1000 + 30 * Day - 1, 30, Day));
Check("exactly the interval is due", Rules.IsDue(1000, 1000 + 30 * Day, 30, Day));
Check("a stamp from the future is not due", !Rules.IsDue(50000, 1000, 30, Day));
Check("zero interval never fires", !Rules.IsDue(1000, 1e9, 0, Day));
Check("zero day length never fires", !Rules.IsDue(1000, 1e9, 30, 0));
Check("days since", Math.Abs(Rules.DaysSince(1000, 1000 + 2.5 * Day, Day) - 2.5) < 1e-9);
Check("days since a future stamp is 0", Rules.DaysSince(5000, 1000, Day) == 0);
Check("stamp at time zero is not 'unstamped'", Rules.Stamp(0) != Rules.Unstamped);
Check("stamp floors", Rules.Stamp(1234.9) == 1234);
var set = Rules.ParseList(" Crypt2 ,, troll_cave ,");
Check("list trims and drops blanks", set.Count == 2 && set.Contains("Crypt2") && set.Contains("troll_cave"));
Check("list is case-insensitive", set.Contains("crypt2"));
Check("empty list", Rules.ParseList("").Count == 0 && Rules.ParseList(null).Count == 0);
var defaults = Rules.ParseList(Rules.DefaultExclude);
foreach (var must in new[] { "Mistlands_DvergrBossEntrance1", "Hildir_crypt", "Hildir_cave" })
    Check("default exclude has " + must, defaults.Contains(must));

var inc = Rules.ParseList(Rules.DefaultInclude);
Check("default include is fuling villages, charred fortresses, troll caves",
    inc.Count == 3 && inc.Contains("GoblinCamp2") && inc.Contains("CharredFortress") && inc.Contains("TrollCave02"));
Check("nothing is both included and excluded by default", !inc.Overlaps(defaults));

// The console command: an optional "reset" and an optional radius, in either order.
bool P(string line, out bool reset, out float radius, out string err) => Rules.ParseCommand(line.Split(' '), out reset, out radius, out err);
Check("bare command: status, nearest", P("restless", out var rs, out var rr, out _) && !rs && rr == 0);
Check("reset alone", P("restless reset", out rs, out rr, out _) && rs && rr == 0);
Check("radius alone lists", P("restless 200", out rs, out rr, out _) && !rs && rr == 200);
Check("reset with radius", P("restless reset 150", out rs, out rr, out _) && rs && rr == 150);
Check("radius before reset", P("restless 150 reset", out rs, out rr, out _) && rs && rr == 150);
Check("RESET is case-insensitive", P("restless RESET", out rs, out _, out _) && rs);
Check("decimal radius, invariant culture", P("restless reset 12.5", out _, out rr, out _) && rr == 12.5f);
Check("a radius over the cap is refused", !P("restless reset 6000", out _, out _, out var e1) && e1.Contains("5000"));
Check("zero radius is refused", !P("restless reset 0", out _, out _, out _));
Check("negative radius is refused", !P("restless reset -5", out _, out _, out _));
Check("junk is refused with usage", !P("restless banana", out _, out _, out var e2) && e2.StartsWith("usage"));

Console.WriteLine($"{passed}/{passed + failed} passed");
return failed == 0 ? 0 : 1;
