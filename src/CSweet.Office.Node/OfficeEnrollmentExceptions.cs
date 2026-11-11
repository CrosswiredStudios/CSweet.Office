namespace CSweet.Office.Node;

/// <summary>Headquarters definitively rejected the one-use enrollment token.</summary>
public sealed class OfficeEnrollmentRejectedException(string message) : InvalidOperationException(message);

/// <summary>A previous token was rejected and no replacement has been installed yet.</summary>
public sealed class OfficeEnrollmentAwaitingTokenException()
    : InvalidOperationException("Office enrollment is waiting for a new connection code.");
