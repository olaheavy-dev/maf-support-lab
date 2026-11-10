namespace Support.Api.Common.Domain;

/// <summary>The kind of failure. Presentation maps each kind to an HTTP status.</summary>
public enum ErrorType
{
    Failure = 0,
    Validation = 1,
    NotFound = 2,
    Conflict = 3,
    Gone = 4,
}
