using AstroLab.Components;
using AstroLab.Services;

// Modo CLI de auto-teste: dotnet run -- tiff-test [path] | phasea <path>
if (args.Length > 0 && args[0] is "tiff-test" or "phasea" or "phaseb" or "fullb" or "bench" or "comatest" or "aitest")
    return SelfTest.Run(args);

var builder = WebApplication.CreateBuilder(args);

const string url = "http://localhost:5151";
builder.WebHost.UseUrls(url);   // porta fixa em qualquer forma de arranque (SPEC/01)

// Servir os Static Web Assets (CSS com scopo, wwwroot) mesmo fora de Development —
// senão, lançada pela DLL/exe (ambiente Production), a app aparece sem estilos.
builder.WebHost.UseStaticWebAssets();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton<ProcessingSession>();   // app local single-user

var app = builder.Build();

// App local: abrir o browser automaticamente ao arrancar.
app.Lifetime.ApplicationStarted.Register(() =>
{
    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
    catch { /* sem browser disponível — ignora */ }
});

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
return 0;
