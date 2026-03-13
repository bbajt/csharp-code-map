using Microsoft.Extensions.Configuration;

namespace SampleApp.Api.Services;

public class ConfigConsumer
{
    private readonly IConfiguration _configuration;

    public ConfigConsumer(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string GetConnectionString()
    {
        return _configuration["ConnectionStrings:DefaultDB"]!;
    }

    public int GetMaxRetries()
    {
        return _configuration.GetValue<int>("App:MaxRetries");
    }

    public string GetLogLevel()
    {
        return _configuration.GetSection("Logging:LogLevel:Default").Value!;
    }
}
