namespace ProjectBeacon.Domain.Common;

public abstract class Entity
{
    public Guid Id { get; protected set; }

    protected Entity() { }

    protected Entity(Guid id)
    {
        Id = id;
    }

    public static T New<T>() where T : Entity, new()
    {
        return new T();
    }
}
