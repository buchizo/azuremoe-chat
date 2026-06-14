using AzureMoe.Chat.Web;
using AzureMoe.Chat.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Config
var cfg = new AppConfig();
builder.Configuration.Bind(cfg);
builder.Services.AddSingleton(cfg);

// HTTP
builder.Services.AddScoped(sp =>
    new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// App services (scoped so they share lifetime with the DI scope)
builder.Services.AddScoped<AssetLoader>();
builder.Services.AddScoped<JsGraphStore>();
builder.Services.AddScoped<JsEmbedder>();
builder.Services.AddScoped<JsLlmEngine>();
builder.Services.AddSingleton<QueryAnalyzer>();
builder.Services.AddScoped<RetrievalEngine>(sp => new RetrievalEngine(
    sp.GetRequiredService<JsGraphStore>()));
builder.Services.AddScoped<RagPipeline>(sp => new RagPipeline(
    sp.GetRequiredService<RetrievalEngine>(),
    sp.GetRequiredService<QueryAnalyzer>(),
    sp.GetRequiredService<JsEmbedder>(),
    sp.GetRequiredService<JsLlmEngine>(),
    sp.GetRequiredService<AppConfig>()));

// Register interfaces so pages can inject either way
builder.Services.AddScoped<IGraphStore>(sp => sp.GetRequiredService<JsGraphStore>());
builder.Services.AddScoped<IEmbedder>(sp => sp.GetRequiredService<JsEmbedder>());
builder.Services.AddScoped<ILlmEngine>(sp => sp.GetRequiredService<JsLlmEngine>());

await builder.Build().RunAsync();
