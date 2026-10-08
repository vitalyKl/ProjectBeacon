namespace ProjectBeacon.Domain.Common;

/// <summary>
/// Base type for persisted domain entities. <see cref="New{T}"/> assigns a version-7 <see cref="Guid"/> in <c>Init</c>.
/// Parameterless constructors on derived types exist for EF Core.
/// </summary>
public abstract class Entity
{
    public Guid Id { get; protected set; }

    protected Entity() { }

    /// <summary>Creates <typeparamref name="T"/> and assigns a version-7 <see cref="Guid"/> identifier.</summary>
    public static T New<T>() where T : Entity, new()
    {
        var instance = new T();
        instance.Init();
        return instance;
    }

    protected void Init()
    {
        Id = Guid.CreateVersion7();
    }
}
