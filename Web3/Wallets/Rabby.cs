using z3n8;
using Microsoft.Playwright;
using z3n8.Browser;
using z3nSafe;


public class Rabby
{
    private readonly string _extId ;
    private IPage _page;
    private readonly IBrowserContext _context;
    private readonly int _delay = 108;
    private Db _db ;
    private readonly string _password;
    
    public Rabby( IBrowserContext context, int?acc = 0,  Db db = null, string key = null, string extId = null)
    {
        _db = db;
        _password = _db.Get("evm", "_addresses", where: $"id = {acc}");
        _context = context;
        _extId = (!string.IsNullOrEmpty(extId)) ? extId : "acmacodkjbdgmoleebolmdjonilkdbch";
    }
    
    private async Task EnsurePage()
    {
        if (_page == null || _page.IsClosed)
            _page = await _context.NewPageAsync();
    }
    
    public async Task ProfilePage()
    {
        await EnsurePage();
        await _page.GotoAsync($"chrome-extension://{_extId}/desktop.html#/desktop/profile");
    }
    public async Task Unlock()
    {
        await  ProfilePage();
        await _page.GetByPlaceholder("Enter the Password to Unlock").FillAsync(_password, new() { Timeout = 3000 });
        await _page.GetByRole(AriaRole.Button, new() { Name = "Unlock" }).ClickAsync();
    }
    
    public async Task ImportNew(string key)
    {
        await _context.CloseExtraTabs();
        await EnsurePage(); 
        await _page.GotoAsync($"chrome-extension://{_extId}/index.html#/new-user/guide");
        await _page.GetByRole(AriaRole.Button, new() { Name = "I already have an address" }).ClickAsync();
        await _page.Sleep(_delay);
        await _page.GetByText("Private Key" ).ClickAsync();
        await _page.Sleep(_delay);
        await _page.GetByPlaceholder("Input private key").FillAsync(key);
        await _page.Sleep(_delay);
        await _page.GetByRole(AriaRole.Button, new() { Name = "Confirm" }).ClickAsync();
        await _page.Sleep(_delay);
        await _page.Locator("[id='password']").FillAsync(_password);
        await _page.Sleep(_delay);
        await _page.Locator("[id='confirmPassword']").FillAsync(_password);
        await _page.Sleep(_delay);
        await _page.GetByRole(AriaRole.Button, new() { Name = "Confirm" }).ClickAsync();        
        await _page.Sleep(_delay);
        await _page.GetByRole(AriaRole.Button, new() { Name = "Get Started" }).ClickAsync();
        await _page.Sleep(_delay);
        await _page.GetByRole(AriaRole.Button, new() { Name = "Done" }).ClickAsync();
        await _page.Sleep(_delay);
        Console.WriteLine("imported");
        await _context.CloseExtraTabs();
        await  ProfilePage();
    }
    
}