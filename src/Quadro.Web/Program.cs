using Microsoft.EntityFrameworkCore;
using Quadro.Web.Components;
using Quadro.Web.Data;
using Quadro.Web.Realtime;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSignalR();
builder.Services.AddDbContext<QuadroDb>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Quadro") ?? "Data Source=quadro.db"));
builder.Services.AddSingleton<BoardEngine>();
builder.Services.AddSingleton<PresenceTracker>();
builder.Services.AddSingleton<HubBroadcaster>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<QuadroDb>();
    await db.Database.EnsureCreatedAsync();
    // WAL: leitura não espera escrita, e uma queda no meio da gravação não corrompe o arquivo.
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
}

var engine = app.Services.GetRequiredService<BoardEngine>();
var broadcaster = app.Services.GetRequiredService<HubBroadcaster>();
engine.OpApplied += broadcaster.SendAsync;
await DemoSeed.EnsureAsync(engine);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapHub<BoardHub>(BoardHub.Path);
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
