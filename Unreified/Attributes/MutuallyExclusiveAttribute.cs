namespace Unreified;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public class MutuallyExclusiveAttribute : Attribute
{
    public MutuallyExclusiveAttribute(Type mutex)
    {
        Mutex = new(mutex);
    }

    public MutuallyExclusiveAttribute(string mutex)
    {
        Mutex = new(mutex);
    }

    public MutuallyExclusiveAttribute(object mutex)
    {
        Mutex = new(mutex);
    }

    public Step.StepIO Mutex { get; }
}
