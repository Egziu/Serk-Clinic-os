using Serk.Clinic.Host;

var app = await SerkHost.BuildAsync(args);
if (app is null)
{
    return 0;
}

await app.RunAsync();
return 0;

/// <summary>Marker for WebApplicationFactory.</summary>
public partial class Program
{
}
