using Backend.Api.Apis;
using Backend.Persistence.Data;
using Microsoft.EntityFrameworkCore;
var builder=WebApplication.CreateBuilder(args);
var path=builder.Configuration["DatabasePath"]??"workspace.db";
builder.Services.AddDbContext<AppDbContext>(o=>o.UseSqlite($"Data Source={path}"));
var app=builder.Build();
using(var scope=app.Services.CreateScope()){scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();}
app.MapWorkspaceEndpoints();
app.Run();
public partial class Program{}
