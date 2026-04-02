using z3nIO;
using z3nIO.Browser;

public class Rabby
{
    private readonly string          _extId;
    private readonly IBrowserInstance _instance;
    private readonly string          _password;
    private readonly int             _delay = 108;

    private const string DefaultExtId = "acmacodkjbdgmoleebolmdjonilkdbch";

    public Rabby(IBrowserInstance instance, Db db, int acc = 0, string extId = null)
    {
        _instance = instance;
        _password = db.Get("evm", "_addresses", where: $"id = {acc}");
        _extId    = !string.IsNullOrEmpty(extId) ? extId : DefaultExtId;
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    public void ProfilePage()
        => _instance.Go($"chrome-extension://{_extId}/desktop.html#/desktop/profile", strict: true);

    // ── Actions ───────────────────────────────────────────────────────────────

    public void Unlock()
    {
        ProfilePage();
        _instance.HeSet(("input", "placeholder", "Enter\\ the\\ Password\\ to\\ Unlock", "regexp", 0), _password);
        _instance.HeClick(("button", "innertext", "Unlock", "text", 0));
    }

    public void ImportNew(string key)
    {
        _instance.ClearShit(_extId);
        _instance.Go($"chrome-extension://{_extId}/index.html#/new-user/guide", strict: true);

        _instance.HeClick(("button", "innertext", "I\\ already\\ have\\ an\\ address", "regexp", 0));
        Thread.Sleep(_delay);
        _instance.HeClick(("div",    "innertext", "Private\\ Key",                   "regexp", 0));
        Thread.Sleep(_delay);
        _instance.HeSet  (("input",  "placeholder", "Input\\ private\\ key",         "regexp", 0), key);
        Thread.Sleep(_delay);
        _instance.HeClick(("button", "innertext", "Confirm",                         "text",   0));
        Thread.Sleep(_delay);
        _instance.HeSet  (("password",        "id"), _password);
        Thread.Sleep(_delay);
        _instance.HeSet  (("confirmPassword", "id"), _password);
        Thread.Sleep(_delay);
        _instance.HeClick(("button", "innertext", "Confirm",     "text", 0));
        Thread.Sleep(_delay);
        _instance.HeClick(("button", "innertext", "Get\\ Started","regexp", 0));
        Thread.Sleep(_delay);
        _instance.HeClick(("button", "innertext", "Done",         "text", 0));
        Thread.Sleep(_delay);

        _instance.ClearShit(_extId);
        ProfilePage();
    }
}