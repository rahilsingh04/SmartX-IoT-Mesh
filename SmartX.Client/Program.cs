using SmartX.Client.Components;
using SmartX.Client.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// The API base URL. Override via appsettings.json ("SmartXApi:BaseUrl") if the
// API is hosted somewhere other than the default local port.
var apiBaseUrl = builder.Configuration["SmartXApi:BaseUrl"] ?? "http://localhost:5080";

builder.Services.AddHttpClient<SmartXApiClient>(client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
});

// Scoped = one instance per browser circuit, so these survive page-to-page navigation.
builder.Services.AddScoped<UiState>();
builder.Services.AddScoped<SimulatorService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
