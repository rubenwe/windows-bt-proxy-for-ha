namespace WindowsBtProxy.Setup;

/// <summary>An install, uninstall or key step failed in a way the user needs to hear about; the message is meant to be printed as is.</summary>
public sealed class SetupException : Exception
{
    public SetupException()
        : this("Setup failed.")
    {
    }

    public SetupException(string message)
        : base(message)
    {
    }

    public SetupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
