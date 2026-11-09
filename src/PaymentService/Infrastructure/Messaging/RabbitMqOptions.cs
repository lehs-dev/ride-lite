namespace PaymentService.Infrastructure.Messaging;

/// <summary>
/// Cấu hình broker, đọc từ env RabbitMq__Host / RabbitMq__UserName / RabbitMq__Password
/// (đã khai trong .devcontainer/compose.yaml). Không hardcode password fallback cho Production.
/// </summary>
public class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string VirtualHost { get; set; } = "/";
    public string UserName { get; set; } = "swiftdev";
    public string Password { get; set; } = string.Empty;
}
