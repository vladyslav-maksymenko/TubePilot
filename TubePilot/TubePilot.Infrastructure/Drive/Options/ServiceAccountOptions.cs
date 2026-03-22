using Microsoft.Extensions.Configuration;

namespace TubePilot.Infrastructure.Drive.Options;

public sealed record ServiceAccountOptions
{
    [ConfigurationKeyName("type")]
    public required string Type { get; init; }
    
    [ConfigurationKeyName("project_id")]
    public required string ProjectId { get; init; }
    
    [ConfigurationKeyName("private_key_id")]
    public required string PrivateKeyId { get; init; }
    
    [ConfigurationKeyName("private_key")]
    public required string PrivateKey { get; init; }
    
    [ConfigurationKeyName("client_email")]
    public required string ClientEmail { get; init; }
    
    [ConfigurationKeyName("client_id")]
    public required string ClientId { get; init; }
    
    [ConfigurationKeyName("auth_uri")]
    public required string AuthUri { get; init; }
    
    [ConfigurationKeyName("token_uri")]
    public required string TokenUri { get; init; }
    
    [ConfigurationKeyName("auth_provider_x509_cert_url")]
    public required string AuthProviderX509CertUrl { get; init; }
    
    [ConfigurationKeyName("client_x509_cert_url")]
    public required string ClientX509CertUrl { get; init; }
}