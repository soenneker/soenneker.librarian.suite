using Soenneker.Librarian.Website.Components;
using Soenneker.Quark;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents();
builder.Services.AddQuarkOptionsAsScoped(new QuarkOptions { AutomaticFrameworkResourceLoading = false });
builder.Services.AddQuarkSuiteAsScoped();
WebApplication app = builder.Build();
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>();
app.Run();
