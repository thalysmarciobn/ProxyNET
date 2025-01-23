namespace ProxyNET.Configuration;

public class ForwardingConfig

{
    public string Address { get; set; }

    public int Port { get; set; }

    public ForwardConfig Forward { get; set; }
}