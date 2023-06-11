using System.Runtime.Serialization;

namespace Unreified;

public class DependencyLoopException : Exception
{
    public DependencyLoopException()
    {
    }

    public DependencyLoopException(string message) : base(message)
    {
    }

    public DependencyLoopException(string message, Exception innerException) : base(message, innerException)
    {
    }

    protected DependencyLoopException(SerializationInfo info, StreamingContext context) : base(info, context)
    {
    }
}