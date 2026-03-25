var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres");
var appDb = postgres.AddDatabase("DefaultConnection");

builder.AddProject("voedl-web", "../VoeDl.Web/VoeDl.Web.csproj")
	.WithReference(appDb)
	.WaitFor(appDb);

builder.Build().Run();
