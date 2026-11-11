namespace AppInHouse.SharedServices.Domain;

/// <summary>A business rule of the domain was broken (for example activating a version that is already active).</summary>
public sealed class DomainException(string message) : Exception(message);
