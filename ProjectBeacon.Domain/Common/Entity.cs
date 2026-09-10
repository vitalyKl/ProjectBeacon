namespace ProjectBeacon.Domain.Common;

public abstract class Entity
{
    public Guid Id { get; protected set; }

    protected Entity() { }

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
