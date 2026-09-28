using BasBridgeRegression;

// Регрессии моста ZP → BAS без BAS: фальшивый BAS отвечает на команды по сценарию.
var failures = 0;
void Expect(string name, bool ok, string detail = "")
{
    if (ok) { Out.P($"ok   {name}"); return; }
    Out.P($"FAIL {name} {detail}");
    failures++;
}
async Task Section(string name, Func<Task> body)
{
    try { await body(); }
    catch (Exception ex) { Out.P($"FAIL {name}: {ex.GetType().Name}: {ex.Message}"); failures++; }
}

await Section("session", () => SessionTests.Run(Expect));

Out.P(failures == 0 ? "ALL OK" : $"FAILURES: {failures}");
return failures == 0 ? 0 : 1;
