namespace SpazaHub.Api.Common.Exceptions;

/// <summary>Request failed input validation. Maps to HTTP 400.</summary>
public class AppValidationException : Exception
{
    public AppValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base("One or more validation errors occurred.")
    {
        Errors = errors;
    }

    public IReadOnlyDictionary<string, string[]> Errors { get; }
}

/// <summary>Authentication failed (bad OTP, bad PIN, dead token). Maps to HTTP 401.</summary>
public class AuthenticationFailedException : Exception
{
    public AuthenticationFailedException(string message) : base(message)
    {
    }
}

/// <summary>Requested resource does not exist in the caller's tenant. Maps to HTTP 404.</summary>
public class NotFoundException : Exception
{
    public NotFoundException(string message) : base(message)
    {
    }
}

/// <summary>Request conflicts with current state (e.g. duplicate). Maps to HTTP 409.</summary>
public class ConflictException : Exception
{
    public ConflictException(string message) : base(message)
    {
    }
}
