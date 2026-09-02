namespace Plinth.Core;

/// <summary>The one exception type Core throws for bad input or bad images.</summary>
public sealed class PlinthException(string message, Exception? inner = null)
    : Exception(message, inner);
