namespace LinguaBridge.Domain.Common;

/// <summary>Base type for entities identified by a <see cref="Guid"/>. Equality is by type and id.</summary>
public abstract class BaseEntity : IEquatable<BaseEntity>
{
    protected BaseEntity()
        : this(Guid.NewGuid())
    {
    }

    protected BaseEntity(Guid id)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Entity id must not be empty.", nameof(id));

        Id = id;
    }

    public Guid Id { get; protected init; }

    public bool Equals(BaseEntity? other) =>
        other is not null && (ReferenceEquals(this, other) || (other.GetType() == GetType() && other.Id == Id));

    public override bool Equals(object? obj) => Equals(obj as BaseEntity);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);

    public static bool operator ==(BaseEntity? left, BaseEntity? right) => Equals(left, right);

    public static bool operator !=(BaseEntity? left, BaseEntity? right) => !Equals(left, right);
}
