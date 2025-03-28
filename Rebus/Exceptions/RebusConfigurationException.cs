using System;

namespace Rebus.Exceptions;

/// <summary>
/// Generic configuration exception to use instead of ConfigurationErrorsException from System.Configuration
/// </summary>
[Serializable]
public class RebusConfigurationException : Exception
{
    /// <summary>
    /// Constructs the exception with the given message
    /// </summary>
    public RebusConfigurationException(string message) :base(message)
    {
    }

    /// <summary>
    /// Constructs the exception with the given message and inner exception
    /// </summary>
    public RebusConfigurationException(Exception innerException, string message) :base(message, innerException)
    {
    }
}