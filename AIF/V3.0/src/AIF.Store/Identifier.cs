namespace AIF.Store;

public sealed class Identifier : IEquatable<Identifier>
{
    public string ImplementerID { get; init; } = string.Empty;

    public string ImplementationID { get; init; } = string.Empty;

    public string AIMName { get; init; } = string.Empty;

    public override string ToString()
    {
        return $"{ImplementerID}{ImplementationID}";
    }

    public bool Equals(Identifier? other)
    {
        if (other is null)
        {
            return false;
        }

        return
            ImplementerID == other.ImplementerID &&
            ImplementationID == other.ImplementationID &&
            AIMName == other.AIMName;
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as Identifier);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            ImplementerID,
            ImplementationID,
            AIMName);
    }
}