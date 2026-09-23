namespace UEBulkExport;

/// <summary>
/// A problem the user can fix, reported as a short headline plus a hint on what to do about it.
/// These never print a stack trace - there is nothing in it worth reading.
/// </summary>
public sealed class UserFacingException(string headline, string? hint = null) : Exception(headline)
{
    public string Headline { get; } = headline;
    public string? Hint { get; } = hint;
}
