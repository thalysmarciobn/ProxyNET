namespace ProxyNET.Configuration;

public class ForwardingConfig

{
    public required string Address { get; set; }

    public required int Port { get; set; }

    public required int ReadTimeout { get; set; }

    public required int WriteTimeout { get; set; }

    public required ForwardConfig Forward { get; set; }
}